using Ouroboros.WebClient;

await using var app = WebClientHost.Build(args);
app.UsePipeline();
app.Run();
return 0;
