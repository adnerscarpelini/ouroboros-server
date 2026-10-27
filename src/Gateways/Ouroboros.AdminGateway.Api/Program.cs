using Ouroboros.AdminGateway.Api;

await using var app = AdminGatewayHost.Build(args);
app.UsePipeline();
app.Run();
return 0;
