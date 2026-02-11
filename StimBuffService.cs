using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal;

[Injectable]
public sealed class StimBuffService(ILogger<StimBuffService> logger)
{
    public void Apply(DatabaseService databaseService, string modRoot)
    {
        Apply(databaseService, modRoot, null);
    }

    public void Apply(DatabaseService databaseService, string modRoot, SalcosArsenalMod.Settings? settings)
    {
        if (databaseService == null)
            return;

        if (string.IsNullOrWhiteSpace(modRoot))
            return;

        var buffsDir = Path.Combine(modRoot, "StimBuffs");
        if (!Directory.Exists(buffsDir))
            return;

        var files = Directory.GetFiles(buffsDir, "*.json", SearchOption.TopDirectoryOnly);
        if (files.Length == 0)
            return;

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        IDictionary buffsDict;
        try
        {
            var tables = databaseService.GetTables();
            buffsDict = GetStimBuffsDictionary(tables);
        }
        catch (Exception e)
        {
            logger.LogError(e, "[SalcosArsenal] StimBuffService: failed to locate stim buffs dictionary.");
            return;
        }

        var expectedValueType = GetExistingBuffValueType(buffsDict);
        if (expectedValueType == null)
        {
            logger.LogWarning("[SalcosArsenal] StimBuffService: cannot determine buff value type from existing data. Skipping StimBuffs.");
            return;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var applied = 0;
        var skipped = 0;

        foreach (var file in files)
        {
            var key = Path.GetFileNameWithoutExtension(file);
            if (string.IsNullOrWhiteSpace(key))
            {
                skipped++;
                continue;
            }

            try
            {
                var raw = File.ReadAllText(file);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    skipped++;
                    continue;
                }

                var payload = JsonSerializer.Deserialize(raw, expectedValueType, options);
                if (payload == null)
                    throw new InvalidOperationException("Deserialized payload is null.");

                buffsDict[key] = payload;
                applied++;
            }
            catch (Exception e)
            {
                skipped++;
                logger.LogWarning(e, "[SalcosArsenal] StimBuffService: failed to load stim buff '{Key}' from file '{FileName}'.", key, Path.GetFileName(file));
            }
        }

        if (settings?.Debug == true)
        {
            logger.LogInformation("[SalcosArsenal] StimBuffService applied. Applied={Applied} Skipped={Skipped}", applied, skipped);
        }
    }

    private static Type? GetExistingBuffValueType(IDictionary buffsDict)
    {
        foreach (DictionaryEntry entry in buffsDict)
        {
            if (entry.Value == null)
                continue;

            return entry.Value.GetType();
        }

        return null;
    }

    private static IDictionary GetStimBuffsDictionary(object tables)
    {
        var globals = GetMemberValue(tables, "Globals") ?? throw new InvalidOperationException("Tables.Globals not found.");

        var configuration = GetMemberValue(globals, "Configuration");
        var health = configuration != null ? GetMemberValue(configuration, "Health") : null;
        var effects = health != null ? GetMemberValue(health, "Effects") : null;
        var stimulator = effects != null ? GetMemberValue(effects, "Stimulator") : null;

        if (stimulator != null)
        {
            var buffs = GetMemberValue(stimulator, "Buffs");
            if (buffs is IDictionary dict)
                return dict;

            throw new InvalidOperationException("Stimulator.Buffs is not an IDictionary.");
        }

        throw new InvalidOperationException("Globals.Configuration.Health.Effects.Stimulator not found.");
    }

    private static object? GetMemberValue(object obj, string name)
    {
        var t = obj.GetType();

        var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null)
            return prop.GetValue(obj);

        var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
            return field.GetValue(obj);

        return null;
    }
}
