using YamlDotNet.Serialization;

namespace MsftLearnToDocx.Models;

// --- YAML deserialization models ---

public class LearningPathYaml
{
    [YamlMember(Alias = "uid")]
    public string Uid { get; set; } = "";

    [YamlMember(Alias = "title")]
    public string Title { get; set; } = "";

    [YamlMember(Alias = "summary")]
    public string Summary { get; set; } = "";

    [YamlMember(Alias = "modules")]
    public List<string> Modules { get; set; } = [];
}

public class ModuleYaml
{
    [YamlMember(Alias = "uid")]
    public string Uid { get; set; } = "";

    [YamlMember(Alias = "title")]
    public string Title { get; set; } = "";

    [YamlMember(Alias = "summary")]
    public string Summary { get; set; } = "";

    [YamlMember(Alias = "units")]
    public List<string> Units { get; set; } = [];
}

public class UnitYaml
{
    [YamlMember(Alias = "uid")]
    public string Uid { get; set; } = "";

    [YamlMember(Alias = "title")]
    public string Title { get; set; } = "";

    [YamlMember(Alias = "durationInMinutes")]
    public int DurationInMinutes { get; set; }

    [YamlMember(Alias = "content")]
    public string Content { get; set; } = "";

    /// <summary>
    /// Quiz data for knowledge-check units (appears as root-level key in YAML).
    /// </summary>
    [YamlMember(Alias = "quiz")]
    public QuizData? Quiz { get; set; }
}

// --- Quiz models (parsed from unit content field) ---

public class QuizWrapper
{
    [YamlMember(Alias = "quiz")]
    public QuizData? Quiz { get; set; }
}

public class QuizData
{
    [YamlMember(Alias = "title")]
    public string Title { get; set; } = "";

    [YamlMember(Alias = "questions")]
    public List<QuizQuestion> Questions { get; set; } = [];
}

public class QuizQuestion
{
    [YamlMember(Alias = "content")]
    public string Content { get; set; } = "";

    [YamlMember(Alias = "choices")]
    public List<QuizChoice> Choices { get; set; } = [];
}

public class QuizChoice
{
    [YamlMember(Alias = "content")]
    public string Content { get; set; } = "";

    [YamlMember(Alias = "isCorrect")]
    public bool IsCorrect { get; set; }

    [YamlMember(Alias = "explanation")]
    public string Explanation { get; set; } = "";
}

// --- Catalog API models ---

public class CatalogModuleResponse
{
    public List<CatalogModule> Modules { get; set; } = [];
}

public class CatalogModule
{
    public string Uid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Units { get; set; } = [];
}

public class CatalogLearningPathResponse
{
    public List<CatalogLearningPath> LearningPaths { get; set; } = [];
}

public class CatalogLearningPath
{
    public string Uid { get; set; } = "";
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public List<string> Modules { get; set; } = [];
}

// --- GitHub API models ---

public class GitHubContentItem
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Type { get; set; } = "";
}

// --- Downloaded content models ---

public class DownloadedContent
{
    public string Title { get; set; } = "";
    public bool IsPath { get; set; }
    public List<DownloadedModule> Modules { get; set; } = [];
}

public class DownloadedModule
{
    public string Title { get; set; } = "";
    public string Uid { get; set; } = "";
    public List<DownloadedUnit> Units { get; set; } = [];
}

public class DownloadedUnit
{
    public string Title { get; set; } = "";
    public string Uid { get; set; } = "";
    public string MarkdownContent { get; set; } = "";
    public bool IsQuiz { get; set; }
}
