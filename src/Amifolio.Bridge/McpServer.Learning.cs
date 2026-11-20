using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal sealed partial class McpServer
{
    private Func<Task<object>>? LearningOperation(string name,JsonElement args)
    {
        switch(name)
        {
            case "project_resolve":Only(args,"cwd");var cwd=Required(args,"cwd",4096);return()=>operations.ResolveLearningProjectAsync(cwd);
            case "learning_status":Only(args,"projectId");var statusProject=Required(args,"projectId",128);return()=>operations.LearningStatusAsync(statusProject);
            case "learning_capture":
                Only(args,"projectId","sessionId","turnId","summary","outcome","quotes","outputs");
                var capture=new LearningCapture(Required(args,"projectId",128),Required(args,"sessionId",128),Required(args,"turnId",128),Required(args,"summary",1500),Required(args,"outcome",24),Strings(args,"quotes",4,1000),Outputs(args));
                return()=>operations.LearningCaptureAsync(capture);
            case "learning_claim":Only(args,"projectId");var claimProject=Required(args,"projectId",128);return()=>operations.LearningClaimAsync(claimProject);
            case "learning_complete":
                Only(args,"jobId","token","proposals");var job=Required(args,"jobId",128);var token=Required(args,"token",128);
                if(!args.TryGetProperty("proposals",out var values)||values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>24)throw new BridgeException("invalid_arguments","proposals must be an array up to 24 items.");
                var proposals=new List<MemoryProposal>();
                foreach(var item in values.EnumerateArray())
                {
                    if(item.ValueKind!=JsonValueKind.Object)throw new BridgeException("invalid_arguments","Expected proposal object.");
                    Only(item,"kind","dimension","value","text","scope","condition","evidenceIds");
                    proposals.Add(new(Required(item,"kind",24),Required(item,"dimension",32),Required(item,"value",256),Required(item,"text",1000),Required(item,"scope",16),Optional(item,"condition",128,true)??"",Strings(item,"evidenceIds",24,128)));
                }
                return()=>operations.LearningCompleteAsync(job,token,proposals);
            case "learning_context":
                Only(args,"projectId","layer","condition");var project=Optional(args,"projectId",128);var layer=Optional(args,"layer",24)??"overview";var condition=Optional(args,"condition",128,true)??"";
                return()=>operations.LearningContextAsync(project,layer,condition);
            default:return null;
        }
    }
    private static string[] Strings(JsonElement args,string name,int count,int maximum)
    {
        if(!args.TryGetProperty(name,out var values))return [];
        if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>count)throw new BridgeException("invalid_arguments",name+" exceeds its item limit.");
        return values.EnumerateArray().Select(x=>x.ValueKind==JsonValueKind.String?BridgeOperations.Text(x.GetString()!,name,maximum):throw new BridgeException("invalid_arguments","Expected string item.")).ToArray();
    }
    private static LearningOutput[] Outputs(JsonElement args)
    {
        if(!args.TryGetProperty("outputs",out var values))return [];
        if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>12)throw new BridgeException("invalid_arguments","outputs exceeds 12 items.");
        return values.EnumerateArray().Select(x=>
        {
            if(x.ValueKind!=JsonValueKind.Object)throw new BridgeException("invalid_arguments","Expected output object.");
            Only(x,"name","purpose","referenceId");return new LearningOutput(Required(x,"name",128),Required(x,"purpose",300),Optional(x,"referenceId",128));
        }).ToArray();
    }
    private static object[] LearningToolDefinitions()
    {
        static object S(int max,int min=1)=>new{type="string",minLength=min,maxLength=max};
        static object A(object items,int max)=>new{type="array",items,maxItems=max};
        static object O(Dictionary<string,object> properties,string[] required)=>new{type="object",properties,required,additionalProperties=false};
        static object T(string name,string description,Dictionary<string,object> properties,string[] required,bool readOnly)=>new{name,description,inputSchema=O(properties,required),annotations=new{readOnlyHint=readOnly,destructiveHint=false,openWorldHint=false}};
        return
        [
            T("project_resolve","Resolve the actual current workspace directory to an exact discovered Codex project ID. Metadata only; no originals, arbitrary traversal or scope fallback.",new(){["cwd"]=S(4096)},["cwd"],true),
            T("learning_status","Read consent, processor, threshold, durable receipt metadata and bounded authorized user-turn identities. Does not expose private profile text or enable consent. sessionHint is not caller authentication; do not guess missing evidence.",new(){["projectId"]=S(128)},["projectId"],true),
            T("learning_capture","After an authorized user turn, immediately store one brief Agent-reported summary and output purpose list. Idempotent by exact project/session/turn; server checks actual user events and quotes. Pending evidence never counts or activates memory. Does not read or modify project originals or authorize model sends.",
                new(){["projectId"]=S(128),["sessionId"]=S(128),["turnId"]=S(128),["summary"]=S(1500),["outcome"]=new{type="string",@enum=new[]{"completed","partial","failed","no_change"}},["quotes"]=A(S(1000),4),["outputs"]=A(O(new(){["name"]=S(128),["purpose"]=S(300),["referenceId"]=S(128)},["name","purpose"]),12)},["projectId","sessionId","turnId","summary","outcome"],false),
            T("learning_claim","Claim one bounded batch only in chat mode and only after the configured threshold/age. Returns a single lease, authorized summaries and user quote IDs for this project; no global raw history. If background mode, missing consent or no batch, leave pending. No API Key needed.",new(){["projectId"]=S(128)},["projectId"],false),
            T("learning_complete","Commit one owned chat lease and at most24 proposals. Cite only supplied user evidence IDs, never Agent summary/echo/silence. Server rechecks source, permissions, mode, versions, suppression and manual locks. Late/duplicate results cannot overwrite data. Does not grant consent or write project files.",
                new(){["jobId"]=S(128),["token"]=S(128),["proposals"]=A(O(new(){["kind"]=S(24),["dimension"]=S(32),["value"]=S(256),["text"]=S(1000),["scope"]=S(16),["condition"]=S(128,0),["evidenceIds"]=A(S(128),24)},["kind","dimension","value","text","scope","evidenceIds"]),24)},["jobId","token","proposals"],false),
            T("learning_context","Read minimal enabled learning context, overview first. Only active applicable entries; no candidates, tombstones or private evidence. Optional condition must match the current task. Global excludes project facts; current instructions always win. Sharing and master context must both be enabled.",new(){["projectId"]=S(128),["layer"]=new{type="string",@enum=new[]{"overview","memory","style","summary"}},["condition"]=S(128,0)},[],true)
        ];
    }
}
