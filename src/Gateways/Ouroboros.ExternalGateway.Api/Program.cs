using Ouroboros.ExternalGateway.Api;

await using var app = ExternalGatewayHost.Build(args);
app.UsePipeline();
app.Run();
return 0;
