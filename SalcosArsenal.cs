using System.Reflection;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services;
using WTTServerCommonLib;
using Range = SemanticVersioning.Range;
using Path = System.IO.Path;

namespace SalcosArsenal;

public sealed record ModMetadata : AbstractModMetadata
{
    public override string ModGuid { get; init; } = "de.salco.salcosarsenal";
    public override string Name { get; init; } = "Salco’s Arsenal";
    public override string Author { get; init; } = "Salco";
    public override SemanticVersioning.Version Version { get; init; } = new("2.0.0");
    public override Range SptVersion { get; init; } = new("~4.0.3");

    public override string License { get; init; } = "MIT";
    public override bool? IsBundleMod { get; init; } = false;

    public override string? Url { get; init; } = null;
    public override List<string>? Contributors { get; init; } = null;
    public override List<string>? Incompatibilities { get; init; } = null;

    public override Dictionary<string, Range>? ModDependencies { get; init; } = new()
    {
        ["com.wtt.commonlib"] = new Range("~2.0.15"),
        ["com.wtt.contentbackport"] = new Range("~1.0.4"),
    };
}

[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostDBModLoader + 20)]
public sealed class SalcosArsenalMod(
    WTTServerCommonLib.WTTServerCommonLib wttCommon,
    DatabaseService databaseService,
    StimBuffService stimBuffService,
    ILogger<SalcosArsenalMod> logger
) : IOnLoad
{
    private static bool _loaded;

    public async Task OnLoad()
    {
        if (_loaded)
        {
            logger.LogWarning("[SalcosArsenal] Duplicate OnLoad call detected. Skipping.");
            return;
        }

        _loaded = true;

        var assembly = Assembly.GetExecutingAssembly();
        var modRoot = Path.GetDirectoryName(assembly.Location) ?? string.Empty;

        var settings = SettingsLoader.LoadOrDefault(modRoot, logger);
        var report = new StartupReport();

        await WttLoadModule.TryLoadCustomItemsAsync(wttCommon, modRoot, settings, logger, report);
        await RecipesModule.TryLoadRecipesAsync(wttCommon, assembly, modRoot, settings, logger, report);

        CompatModule.TryApply(databaseService, stimBuffService, modRoot, settings, logger, report);

        if (settings.Debug)
        {
            report.Emit(logger);
        }

        logger.LogInformation("[SALCO'S ARSENAL LOADED SUCCESSFULLY]");
    }

    private sealed class StartupReport
    {
        public int ItemsFoldersLoaded { get; set; }
        public int RecipeFoldersLoaded { get; set; }
        public int CompatModulesApplied { get; set; }
        public int Warnings { get; set; }

        public void Emit(ILogger log)
        {
            log.LogInformation("[SalcosArsenal] Items folders loaded: {Count}", ItemsFoldersLoaded);
            log.LogInformation("[SalcosArsenal] Recipe folders loaded: {Count}", RecipeFoldersLoaded);
            log.LogInformation("[SalcosArsenal] Compat modules applied: {Count}", CompatModulesApplied);
            if (Warnings > 0)
                log.LogWarning("[SalcosArsenal] Warnings: {Count}", Warnings);
        }
    }

    public sealed class Settings
    {
        public bool EnableWeaponsCompat { get; init; } = true;
        public bool EnableAmmoCompat { get; init; } = true;
        public bool EnablePlatesCompat { get; init; } = true;
        public bool EnableStimBuffs { get; init; } = true;
        public bool Debug { get; init; } = false;
        public bool StrictMode { get; init; } = false;

        public static Settings Default => new();
    }

    private static class SettingsLoader
    {
        public static Settings LoadOrDefault(string modRoot, ILogger log)
        {
            try
            {
                var settingsPath = Path.Combine(modRoot, "config", "settings.json");
                if (!File.Exists(settingsPath))
                    return Settings.Default;

                var json = File.ReadAllText(settingsPath);
                var settings = System.Text.Json.JsonSerializer.Deserialize<Settings>(json);
                return settings ?? Settings.Default;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[SalcosArsenal] Failed to load settings.json. Using defaults.");
                return Settings.Default;
            }
        }
    }

    private static class WttLoadModule
    {
        private static readonly HashSet<string> DbFolderSkip = new(StringComparer.OrdinalIgnoreCase)
        {
            "CustomHideoutRecipes"
        };

        public static async Task TryLoadCustomItemsAsync(
            WTTServerCommonLib.WTTServerCommonLib wttCommon,
            string modRoot,
            Settings settings,
            ILogger log,
            StartupReport report
        )
        {
            var dbRoot = Path.Combine(modRoot, "db");
            if (!Directory.Exists(dbRoot))
                return;

            var subDirs = Directory.GetDirectories(dbRoot);
            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);

            foreach (var absSubDir in subDirs)
            {
                var folderName = Path.GetFileName(absSubDir);
                if (string.IsNullOrWhiteSpace(folderName))
                    continue;

                if (DbFolderSkip.Contains(folderName))
                    continue;

                var relative = Path.Combine("db", folderName);

                try
                {
                    await wttCommon.CustomItemServiceExtended.CreateCustomItems(Assembly.GetExecutingAssembly(), relative);
                    report.ItemsFoldersLoaded++;
                }
                catch (Exception ex)
                {
                    report.Warnings++;
                    log.LogWarning(ex, "[SalcosArsenal] Failed to load WTT custom items from folder '{Folder}'.", relative);
                    if (settings.StrictMode)
                        throw;
                }
            }
        }
    }

    private static class RecipesModule
    {
        public static async Task TryLoadRecipesAsync(
            WTTServerCommonLib.WTTServerCommonLib wttCommon,
            Assembly assembly,
            string modRoot,
            Settings settings,
            ILogger log,
            StartupReport report
        )
        {
            var recipesFolder = Path.Combine(modRoot, "db", "CustomHideoutRecipes");
            if (!Directory.Exists(recipesFolder))
                return;

            try
            {
                await wttCommon.CustomHideoutRecipeService.CreateHideoutRecipes(assembly);
                report.RecipeFoldersLoaded++;
            }
            catch (Exception ex)
            {
                report.Warnings++;
                log.LogWarning(ex, "[SalcosArsenal] Failed to load hideout recipes.");
                if (settings.StrictMode)
                    throw;
            }
        }
    }

    private static class CompatModule
    {
        private static bool _applied;

        public static void TryApply(
            DatabaseService databaseService,
            StimBuffService stimBuffService,
            string modRoot,
            Settings settings,
            ILogger log,
            StartupReport report
        )
        {
            if (_applied)
                return;

            _applied = true;

            TryApplyBlock(
                settings.EnableWeaponsCompat,
                "WeaponsCompat",
                () => WeaponsCompatModule.Apply(databaseService, modRoot, settings, log),
                settings,
                log,
                report
            );

            TryApplyBlock(
                settings.EnableAmmoCompat,
                "AmmoCompat",
                () => AmmoCompatModule.Apply(databaseService, settings, log),
                settings,
                log,
                report
            );

            TryApplyBlock(
                settings.EnablePlatesCompat,
                "PlatesCompat",
                () => PlatesCompatModule.Apply(databaseService, settings, log),
                settings,
                log,
                report
            );

            TryApplyBlock(
                settings.EnableStimBuffs,
                "StimBuffs",
                () => stimBuffService.Apply(databaseService, modRoot, settings),
                settings,
                log,
                report
            );
        }

        private static void TryApplyBlock(
            bool enabled,
            string name,
            Action action,
            Settings settings,
            ILogger log,
            StartupReport report
        )
        {
            if (!enabled)
            {
                if (settings.Debug)
                    log.LogInformation("[SalcosArsenal] Feature '{Name}' disabled by settings.", name);
                return;
            }

            try
            {
                action();
                report.CompatModulesApplied++;
            }
            catch (Exception ex)
            {
                report.Warnings++;
                log.LogWarning(ex, "[SalcosArsenal] Feature '{Name}' failed. Disabled for this run.", name);
                if (settings.StrictMode)
                    throw;
            }
        }
    }
}
