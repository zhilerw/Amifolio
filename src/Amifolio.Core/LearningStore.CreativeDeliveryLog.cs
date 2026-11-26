using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

public sealed record CreativeDeliveryLogEntry(DateTimeOffset ObservedAt, string ProjectId, string SessionId, bool Confirmed);

public sealed partial class LearningStore
{
    private const int CreativeDeliveryLogLimit = 50;

    public async Task<IReadOnlyList<CreativeDeliveryLogEntry>> ReadCreativeDeliveryLogAsync(
        string? projectId, CancellationToken ct = default)
    {
        var scope = Key(projectId);
        await using var lease = await StorageLease.SharedAsync(root, ct).ConfigureAwait(false);
        return await Task.Run<IReadOnlyList<CreativeDeliveryLogEntry>>(() =>
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.Combine(root, "learning", "state.sqlite");
            ArchiveSafety.NoReparse(path);
            if (!File.Exists(path))
            {
                var marker = Path.Combine(root, "learning", "initialized.marker");
                ArchiveSafety.NoReparse(marker);
                if (initialized || File.Exists(marker))
                    throw new IOException("学习数据库缺失，无法读取提供日志。");
                return [];
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            // Databases created before the creative hook can lack this table.
            if (LearningSchema.Scalar(connection, transaction,
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='creative_delivery'") is null)
                return [];

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // The same turn can receive both default and project preferences.
            // Collapse these rows when their confirmation status agrees.
            command.CommandText = """
                SELECT MAX(observed), project, session, confirmed
                FROM creative_delivery
                WHERE $project IS NULL OR project = $project
                GROUP BY project, session, turn, confirmed
                ORDER BY MAX(observed) DESC, project, session, turn, confirmed DESC
                LIMIT $limit
                """;
            command.Parameters.AddWithValue("$project", scope == "global" ? DBNull.Value : scope);
            command.Parameters.AddWithValue("$limit", CreativeDeliveryLogLimit);

            using var rows = command.ExecuteReader();
            var entries = new List<CreativeDeliveryLogEntry>();
            while (rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                entries.Add(new(DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(0)),
                    rows.GetString(1), rows.GetString(2), rows.GetBoolean(3)));
            }
            return entries;
        }, ct).ConfigureAwait(false);
    }
}
