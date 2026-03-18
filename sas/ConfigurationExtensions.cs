using Microsoft.Extensions.Configuration;
using Tomlyn.Extensions.Configuration;

namespace sas;

public static class ConfigurationExtensions
{
    const string OverrideTomlPathKey = "Override";

    public static IConfigurationBuilder AddTomlFileWithOverride(this IConfigurationBuilder configurationBuilder, string path, bool optional, bool reloadOnChange, string? basePath = null)
    {
        var absolutePath = Path.GetFullPath(path, basePath ?? AppContext.BaseDirectory);

        configurationBuilder.AddTomlFile(absolutePath, optional, reloadOnChange);

        var configuration = new ConfigurationBuilder().AddTomlFile(absolutePath, optional, reloadOnChange).Build();
        var overrideTomlPath = configuration[OverrideTomlPathKey];

        if (!string.IsNullOrWhiteSpace(overrideTomlPath))
        {
            string absoluteOverrideTomlPath = Path.GetFullPath(overrideTomlPath, basePath ?? AppContext.BaseDirectory);
            if (File.Exists(absoluteOverrideTomlPath))
            {
                configurationBuilder.AddTomlFileWithOverride(absoluteOverrideTomlPath, optional, reloadOnChange, basePath: Path.GetDirectoryName(absoluteOverrideTomlPath));
            }
        }

        return configurationBuilder;
    }
}
