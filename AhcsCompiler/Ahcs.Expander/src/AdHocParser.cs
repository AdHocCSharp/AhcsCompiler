// ReSharper disable UnusedMember.Global
// ReSharper disable EmptyNamespace
// ReSharper disable NullableWarningSuppressionIsUsed

// ReSharper disable SuggestBaseTypeForParameter
namespace Ahcs.Expander;

using System.Reflection;

using LibGit2Sharp;

using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

public class AdHocParser
{
    internal const string PROJECT_SDK_MICROSOFT_NET_SDK = "<Project Sdk=\"Microsoft.NET.Sdk\" />";

    private readonly Signature _identity =
        new("AdHocCSharp", "a@b.com", DateTimeOffset.Now);

    private ILogger<AdHocParser> Logger
    {
        get;
    }

    public AdHocParser(ILogger<AdHocParser> logger, MSBuildWorkspace workspace)
    {
        Logger = logger;
        Workspace = workspace;
    }

    public (bool result, Commit? commit) ProcessFile(string file, DirectoryInfo outputPathInfo)
        => ProcessFile(file, outputPathInfo.FullName);

    public (bool result, Commit? commit) ProcessFile(string file, string outputPath)
    {
        string logTemplate = $"[{MethodBase.GetCurrentMethod()?.Name}] {0}";
        string? result = null;

        try
        {
            DirectoryInfo outputInfo = new(outputPath);

            if (!outputInfo.Exists)
            {
                outputInfo.Create();
            }

            Logger.LogInformation($"[ProcessFile] Processing {file}");

            FileInfo fileInfo = new(file);

            FileStream fileStream = fileInfo.OpenRead();

            using StreamReader reader = new(fileStream);
            string programText = reader.ReadToEnd();

            fileStream.Close();

            if (programText is null or "")
            {
                result = string.Format(logTemplate, "programText is null or empty.");

                return (false, null);
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(programText);

            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            var fileExpanded = false;
            Commit? commit = null;

            foreach (SyntaxTrivia item in root.GetLeadingTrivia())
            {
                SyntaxKind kind = item.Kind();
                Logger.LogInformation($"kind: {kind}");
                (bool result, Commit? commit) processed = kind switch
                {
                    SyntaxKind.SingleLineDocumentationCommentTrivia => root.ProcessToken(
                            Workspace,
                            fileInfo,
                            item,
                            outputInfo,
                            _identity,
                            PROJECT_SDK_MICROSOFT_NET_SDK,
                            Logger),
                    _ => (false, null),
                };

                if (!processed.result)
                {
                    continue;
                }

                result = string.Format(logTemplate, $"Processed {file}");
                fileExpanded = true;
                commit = processed.commit;

                break;
            }

            if (commit is null)
            {
                result = string.Format(logTemplate, $"Nothing to do in {file}");
            }

            return (fileExpanded, commit);
        }
        finally
        {
            Logger.LogInformation(result ??
                            string.Format(logTemplate, $"No result specified in {file}.")
            );
        }
    }

    public MSBuildWorkspace Workspace
    {
        get;
        // ReSharper disable once AutoPropertyCanBeMadeGetOnly.Local
        private set;
    }

    public Solution Solution
        => Workspace.CurrentSolution;
}
