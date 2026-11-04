using System.Text;
using PrBuddy.Cli;
using Spectre.Console;

Console.OutputEncoding = Encoding.UTF8;
return CliApp.Run(args, AnsiConsole.Console, Console.Out, Console.Error);
