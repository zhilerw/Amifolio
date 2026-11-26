using System.Reflection;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    public string ReleaseVersion { get; } = typeof(MainWindow).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
}
