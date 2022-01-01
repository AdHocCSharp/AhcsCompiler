// ReSharper disable UnusedMember.Global
// ReSharper disable EmptyNamespace
// ReSharper disable NullableWarningSuppressionIsUsed

// ReSharper disable SuggestBaseTypeForParameter
namespace Ahcs.Expander;

using LibGit2Sharp;

using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

public class AdHocParser
{
    private const string DEFAULT_XML = "<Project />";

    private ILogger<AdHocParser> Logger
    {
        get;
    }

    public AdHocParser(ILogger<AdHocParser> logger, Repository gitRepo, MSBuildWorkspace workspace)
    {
        Logger = logger;
        GitRepo = gitRepo;
        Workspace = workspace;
    }

    public string YamlToXml(string yaml)
    {
        IDeserializer deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        object? project = deserializer
            .Deserialize(new StringReader(yaml));

        if (project is null or "")
        {
            return DEFAULT_XML;
        }

        ISerializer serializer = new SerializerBuilder()
            .JsonCompatible()
            .Build();
        string json = serializer.Serialize(project);

        while (json.Contains("\"_", Ordinal))
        {
            json = json.Replace("\"_", "\"@", Ordinal);
        }

        XDocument? xml = JsonConvert.DeserializeXNode(json);

        return xml?.ToString() ?? DEFAULT_XML;
    }

    internal bool ProcessFile(string file)
    {
        const string RESULT_TEMPLATE = "[ProcessFile] {0}";
        string? result = null;

        try
        {
            Logger.LogInformation($"[ProcessFile] Processing {file}");

            FileInfo fileInfo = new(file);

            FileStream fileStream = fileInfo.OpenRead();

            using StreamReader reader = new(fileStream);
            string programText = reader.ReadToEnd();

            fileStream.Close();

            if (programText is null or "")
            {
                result = string.Format(RESULT_TEMPLATE, "programText is null or empty.");

                return false;
            }

            SyntaxTree tree = CSharpSyntaxTree.ParseText(programText);

            CompilationUnitSyntax root = tree.GetCompilationUnitRoot();

            var fileExpanded = false;

            foreach (SyntaxTrivia item in root.GetLeadingTrivia())
            {
                SyntaxKind kind = item.Kind();
                Logger.LogInformation($"kind: {kind}");
                bool processed = kind switch
                {
                    SyntaxKind.SingleLineDocumentationCommentTrivia =>
                        ProcessToken(fileInfo, item, root),
                    _ => false,
                };

                if (!processed)
                {
                    continue;
                }

                result = string.Format(RESULT_TEMPLATE, $"Processed {file}");
                fileExpanded = true;
            }

            result = string.Format(RESULT_TEMPLATE, $"Nothing to do in {file}");

            return fileExpanded;
        }
        finally
        {
            Logger.LogInformation(result ??
                            string.Format(RESULT_TEMPLATE, $"No result specified in {file}.")
            );
        }
    }

    private List<SyntaxNode> GetUsingDirectives(CompilationUnitSyntax? root)
    {
        List<SyntaxNode> usingDirectives = new();

        if (root is null)
        {
            return usingDirectives;
        }

        SyntaxToken directive = root.GetFirstToken(includeDirectives: true);

        if (directive.Parent is null)
        {
            directive = directive.GetNextToken(includeDirectives: true);
        }

        while ((directive.Parent?.Kind() ?? SyntaxKind.None) != SyntaxKind.None)
        {
            SyntaxKind? kind = directive.Parent?.Kind();

            Logger.LogInformation($"kind: {kind}");

            bool found = kind switch
            {
                SyntaxKind.UsingDirective => true,
                _ => false,
            };

            if (found)
            {
                SyntaxNode? node = directive.Parent;
                string? code = node?.ToFullString();
                Logger.LogInformation($"Using Directive: [{code}]");

                if (node != null)
                {
                    usingDirectives.Add(node);
                }
            }

            directive = directive.GetNextToken(includeDirectives: true);
        }

        return usingDirectives.Distinct()
            .ToList();
    }

    internal bool ProcessToken(
        FileInfo fileInfo,
        SyntaxTrivia trivia,
        CompilationUnitSyntax root
    )
    {
        Workspace = MSBuildWorkspace.Create();

        SyntaxNode? structure = trivia.GetStructure();

        if (structure == null)
        {
            return false;
        }

        root = root.RemoveNode(structure, SyntaxRemoveOptions.KeepNoTrivia) ?? root;

        string triviaText = trivia.ToFullString();
        IEnumerable<string> lines = triviaText
            .Split('\n', StringSplitOptions.TrimEntries)
            .Select(static l => l.Replace("///", "", Ordinal));

        triviaText = string.Join(Environment.NewLine, lines);

        string? projectXml = GetProjectXml(triviaText, fileInfo);

        if (projectXml is null or "")
        {
            return true;
        }

        string projectDirectoryPath =
            Path.Combine(
                fileInfo.Directory!.FullName,
                fileInfo.Name.Replace(fileInfo.Extension, "",
                    InvariantCultureIgnoreCase
                )
            );

        DirectoryInfo projectDirectory = new(projectDirectoryPath);

        Logger.LogInformation($"[ProcessToken] Expanding Project Directory: {projectDirectory}");

        string projectName = projectDirectory.Name;

        Project? oldProject = Solution.Projects.SingleOrDefault(p => p.Name==projectName);

        if (oldProject is not null)
        {
            Logger.LogInformation(
                $"[ProcessToken] Removing {projectName} from Solution"
            );

            Workspace.TryApplyChanges(
                Solution.RemoveProject(oldProject.Id));
        }

        if (projectDirectory.Exists)
        {
            projectDirectory.Delete(true);
        }

        projectDirectory.Create();

        string csprojFilename =
            Path.Combine(
                projectDirectory.FullName,
                fileInfo.Name.Replace(fileInfo.Extension, ".csproj",
                    InvariantCultureIgnoreCase
                )
            );

        FileInfo projectFile = new(csprojFilename);

        using StreamWriter writer = new(projectFile.OpenWrite());

        writer.Write(projectXml);

        writer.Close();

        if (!projectFile.Exists)
        {
            return false;
        }

        Logger.LogInformation($"[ProcessToken] Created project file: {csprojFilename}");

        root = CreateGlobalUsings(projectDirectory, root);

        FileInfo newFile = AdHocParser.CreateSourceFile(projectFile, fileInfo, root);

        if (!newFile.Exists)
        {
            return false;
        }

        Logger.LogInformation($"[ProcessToken] Wrote Source to {newFile.FullName}");

        bool addResult = AddProjectToSolution(projectFile);

        if (addResult)
        {
            return true;
        }

        string? gitRepoPath = Repository.Init(projectDirectoryPath);
        GitRepo = new Repository(gitRepoPath);

        Signature identity = new("AdHocCSharp", "a@b.com", DateTimeOffset.Now);
        GitRepo.Commit("Created AdHoc Project", identity, identity);

        return false;
    }

    private bool AddProjectToSolution(FileInfo projectFile)
    {
        string newProjectName = projectFile.Name.Replace(
            projectFile.Extension,
            "",
            StringComparison.InvariantCultureIgnoreCase
        );

        ProjectId projectId = ProjectId.CreateNewId(newProjectName);

        Workspace.TryApplyChanges(
            Solution.AddProject(
                ProjectInfo.Create(
                    projectId,
                    VersionStamp.Create(),
                    newProjectName,
                    newProjectName,
                    LanguageNames.CSharp
                )
            )
        );

        Project? project = Solution.GetProject(projectId);

        if (project is null)
        {
            return true;
        }

        Logger.LogInformation(
            $"[ProcessToken] Added new project to Solution: {project.FilePath}"
        );
        Logger.LogInformation(
            $"[ProcessToken] msbuildProject.AllEvaluatedItems.Count: {project.Documents.Count()}"
        );

        return false;
    }

    private static FileInfo CreateSourceFile(FileInfo projectFile, FileInfo fileInfo, CompilationUnitSyntax root)
    {
        string newFileName = Path.Combine(projectFile.DirectoryName!, fileInfo.Name);
        string source = root.ToFullString(); //File.ReadAllText(fileInfo.FullName);

        //source = source.Replace(trivia.ToFullString() , "" , InvariantCultureIgnoreCase);

        File.WriteAllText(newFileName, source);

        FileInfo newFile = new(newFileName);

        return newFile;
    }

    private CompilationUnitSyntax CreateGlobalUsings(
        DirectoryInfo projectDirectory,
        CompilationUnitSyntax root)
    {
        List<SyntaxNode> usingDirectives = GetUsingDirectives(root);

        if (usingDirectives is not
            {
                Count: > 0,
            })
        {
            return root;
        }

        string globalUsingsFileName = Path.Combine(
            projectDirectory.FullName,
            "GlobalUsings.cs"
        );

        string code = string.Join(
            Environment.NewLine,
            usingDirectives.Select(static d => $"global {d.ToFullString().Trim()}")
        );
        File.WriteAllText(globalUsingsFileName, code);

        root = root.RemoveNodes(usingDirectives, SyntaxRemoveOptions.KeepNoTrivia) ?? root;

        return root;
    }

    // ReSharper disable once SuggestBaseTypeForParameter
    private string? GetProjectXml(string trivia, FileInfo fileInfo)
    {
        string? projectXml = null;

        try
        {
            XDocument xml = XDocument.Parse(trivia);

            if (xml.Root?.Name.LocalName is not "Project")
            {
                return null;
            }

            Logger.LogInformation($"[ProcessToken] {fileInfo.Name} has valid Project xml.");

            projectXml = xml.ToString();
        }
        catch
        {
            // Ignore
        }

        projectXml ??= YamlToXml(trivia);

        return projectXml;
    }

    public Repository GitRepo
    {
        get;
        set;
    }

    public MSBuildWorkspace Workspace
    {
        get;
        private set;
    }

    public Solution Solution
        => Workspace.CurrentSolution;
}
