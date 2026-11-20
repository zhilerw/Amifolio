using System.Text.Json;
using Amifolio.Core;

internal sealed partial class BridgeSuite
{
    private async Task LearningTestsAsync()
    {
        await Test("Learning MCP permissions are not granted by a model",async()=>
        {
            await using var rpc=new RpcChild(this);await rpc.Initialize();
            var resolved=Structured(await rpc.Tool("project_resolve",new{cwd=projectRootA}));
            Check(resolved.GetProperty("projectId").GetString()==projectA,"wrong project resolved");
            var status=Structured(await rpc.Tool("learning_status",new{projectId=projectA}));
            Check(!status.GetProperty("captureAllowed").GetBoolean(),"capture granted by install");
            var rejected=await rpc.Tool("learning_capture",new{projectId=projectA,sessionId=Guid.NewGuid().ToString(),turnId="turn-1",summary="synthetic summary",outcome="completed",quotes=Array.Empty<string>(),outputs=Array.Empty<object>()});
            Structured(rejected,true);
            Check(ErrorCode(await rpc.Tool("learning_set_policy",new{capture=true}))==-32602,"model could change permission");
            Check((await new LearningStore(data).SnapshotAsync(projectA)).Summaries.Count==0,"disabled capture left record");
        });
        await Test("Learning MCP durable receipt and single chat lease survive process restart",async()=>
        {
            var learning=new LearningStore(data);await learning.SetPolicyAsync(projectA,new(){Capture=true,Learn=true,AutoActivate=true,BatchSize=1});
            var session=Guid.NewGuid().ToString();var now=DateTimeOffset.UtcNow.ToString("O");var directory=Path.Combine(codexHome,"sessions","2026","11","14");Directory.CreateDirectory(directory);
            await File.WriteAllLinesAsync(Path.Combine(directory,"rollout-"+session+".jsonl"),new object[]{new{timestamp=now,type="session_meta",payload=new{id=session,cwd=projectRootA}},new{timestamp=now,type="turn_context",payload=new{turn_id="turn-1",cwd=projectRootA}},new{timestamp=now,type="event_msg",payload=new{type="user_message",message="以后请简洁回答"}}}.Select(x=>JsonSerializer.Serialize(x)));
            var capture=new{projectId=projectA,sessionId=session,turnId="turn-1",summary="讨论表达偏好。",outcome="completed",quotes=new[]{"以后请简洁回答"},outputs=Array.Empty<object>()};
            long sequence;
            await using(var rpc=new RpcChild(this))
            {
                await rpc.Initialize();var first=Structured(await rpc.Tool("learning_capture",capture));sequence=first.GetProperty("sequence").GetInt64();
                Check(first.GetProperty("state").GetString()=="verified","user event not verified");
            }
            await using(var rpc=new RpcChild(this))
            {
                await rpc.Initialize();var again=Structured(await rpc.Tool("learning_capture",capture));
                Check(again.GetProperty("duplicate").GetBoolean()&&again.GetProperty("sequence").GetInt64()==sequence,"restart duplicated receipt");
                var claimed=Structured(await rpc.Tool("learning_claim",new{projectId=projectA}));var job=claimed.GetProperty("job");
                var jobId=job.GetProperty("Id").GetString();var token=job.GetProperty("Token").GetString();
                var ids=job.GetProperty("Evidence").EnumerateArray().Select(e=>e.GetProperty("Evidence").GetProperty("Id").GetString()!).ToArray();
                var noSecond=Structured(await rpc.Tool("learning_claim",new{projectId=projectA}));Check(noSecond.GetProperty("job").ValueKind==JsonValueKind.Null,"double lease granted");
                Structured(await rpc.Tool("learning_complete",new{jobId,token,proposals=new[]{new{kind="style",dimension="verbosity",value="brief",text="偏好简洁表达",scope="project",condition="",evidenceIds=ids}}}));
                var status=Structured(await rpc.Tool("learning_status",new{projectId=projectA}));Check(status.GetProperty("pending").GetInt32()==0,"completed backlog retained");
                var hidden=Structured(await rpc.Tool("learning_context",new{projectId=projectA,layer="style"}));Check(hidden.GetProperty("documents").EnumerateObject().Count()==0,"learning enabled sharing");
            }
        });
    }
}
