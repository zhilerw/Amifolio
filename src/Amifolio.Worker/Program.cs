using Amifolio.Core;

// No database, provider access or persistent worker loop; EOF terminates the one request.
if(args.Length!=0)return 2;
return await WorkerProtocol.RunAsync(Console.OpenStandardInput(),Console.OpenStandardOutput());
