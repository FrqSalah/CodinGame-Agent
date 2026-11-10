// Lancement local avec le dashboard Aspire : dotnet run --project src/CodinGame.AppHost
// Sur le VPS, c'est docker compose qui lance la ferme et le dashboard (voir deploy/).
var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.CodinGame_Cli>("farm")
    .WithArgs("servir")
    .WithEnvironment("FARM_DATA", Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "farm-data")));

builder.Build().Run();
