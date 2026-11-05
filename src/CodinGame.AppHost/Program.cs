var builder = DistributedApplication.CreateBuilder(args);

// Le Cli est le point d'entrée principal de l'application
// Il contient les agents, l'arène, et toute la logique métier
var cli = builder.AddProject<Projects.CodinGame_Cli>("codingame-cli");

builder.Build().Run();
