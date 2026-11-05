using Spectre.Console;

AnsiConsole.Write(
    new FigletText("CodinGame Farm")
        .Color(Color.Green));

AnsiConsole.MarkupLine("[grey]Autonomous Agent Pipeline for CodinGame Bot Optimization[/]");
AnsiConsole.MarkupLine("[grey]Framework: Microsoft Agent Framework | Orchestrator: .NET Aspire[/]");
AnsiConsole.WriteLine();

// Afficher l'état actuel du système
var table = new Table()
    .Border(TableBorder.Rounded)
    .Title("[bold green]System Status[/]")
    .AddColumn("Component")
    .AddColumn("Status");

table.AddRow("ArenaScheduler", "[green]Ready[/]");
table.AddRow("ExperimentStore", "[green]Ready[/]");
table.AddRow("MatchRunner", "[yellow]Awaiting referee[/]");
table.AddRow("GitRepositoryManager", "[green]Ready[/]");
table.AddRow("OpponentPoolManager", "[green]Ready[/]");
table.AddRow("EloCalculator (SPRT)", "[green]Ready[/]");
table.AddRow("BudgetGuard", "[green]Ready[/]");
table.AddRow("DiskQuotaManager", "[green]Ready[/]");

AnsiConsole.Write(table);
AnsiConsole.WriteLine();

// Afficher les agents
var agentsTable = new Table()
    .Border(TableBorder.Rounded)
    .Title("[bold cyan]Agent Roles (Microsoft Agent Framework)[/]")
    .AddColumn("Agent")
    .AddColumn("Framework Class")
    .AddColumn("Mission");

agentsTable.AddRow("Admin", "ChatClientAgent", "Provision pipelines, health monitoring");
agentsTable.AddRow("Worker/Coder", "ChatClientAgent", "Implement bot code modifications");
agentsTable.AddRow("Reviewer", "ChatClientAgent", "PROMOTE / ROLLBACK / TUNE_PARAMS decisions");
agentsTable.AddRow("Researcher", "ChatClientAgent", "Architectural pivots on Elo plateau");
agentsTable.AddRow("Utils-Writer", "ChatClientAgent", "Generate deterministic parsers & simulators");

AnsiConsole.Write(agentsTable);
