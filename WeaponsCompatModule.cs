using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;
using Path = System.IO.Path;

namespace SalcosArsenal;

public static class WeaponsCompatModule
{
    private static bool _applied;

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static void Apply(DatabaseService databaseService, string modRoot, SalcosArsenalMod.Settings settings, ILogger log)
    {
        if (_applied)
        {
            if (settings.Debug)
            {
                log.LogInformation("[SalcosArsenal] WeaponsCompat already applied. Skipping.");
            }

            return;
        }

        _applied = true;

        var items = databaseService.GetItems();
        var rules = LoadRules(modRoot, log, settings);

        if (rules.Count == 0)
        {
            if (settings.Debug)
            {
                log.LogInformation("[SalcosArsenal] No weapon compat rules found.");
            }

            return;
        }

        var ammoByCaliber = BuildAmmoByCaliberIndex(items);

        var patchedWeapons = 0;
        var patchedWeaponSlots = 0;
        var patchedWeaponAmmoFilters = 0;

        foreach (var rule in rules)
        {
            if (!items.TryGetValue(rule.WeaponTpl, out var weaponTemplate) || weaponTemplate.Properties is null)
            {
                log.LogWarning("[SalcosArsenal] WeaponsCompat: weaponTpl '{Tpl}' not found. Rule skipped.", rule.WeaponTpl);
                continue;
            }

            var desiredCaliber = rule.CaliberOverride ?? weaponTemplate.Properties.AmmoCaliber;
            if (string.IsNullOrWhiteSpace(desiredCaliber))
            {
                log.LogWarning("[SalcosArsenal] WeaponsCompat: weaponTpl '{Tpl}' has no ammoCaliber and no caliberOverride. Rule skipped.", rule.WeaponTpl);
                continue;
            }

            // Apply caliber override (explicit).
            if (!string.IsNullOrWhiteSpace(rule.CaliberOverride))
            {
                weaponTemplate.Properties.AmmoCaliber = desiredCaliber;
            }

            // Resolve ammo list for this caliber.
            ammoByCaliber.TryGetValue(desiredCaliber, out var ammoTplsForCaliber);
            ammoTplsForCaliber ??= new List<MongoId>();

            if (rule.AllowAmmoByCaliber)
            {
                patchedWeaponAmmoFilters += PatchWeaponAmmoFilters(weaponTemplate, ammoTplsForCaliber, rule.ExcludeAmmoTpls);
            }

            if (rule.AllowMagazinesByCaliber)
            {
                patchedWeaponSlots += PatchWeaponMagazineSlot(
                    weaponTemplate,
                    desiredCaliber,
                    rule.ExcludeMagazineTpls,
                    items
                );
            }

            patchedWeapons++;
        }

        log.LogInformation(
            "[SalcosArsenal] WeaponsCompat applied. Rules: {Rules}, Weapons: {Weapons}, SlotEntriesAdded: {Slots}, AmmoEntriesAdded: {Ammo}",
            rules.Count,
            patchedWeapons,
            patchedWeaponSlots,
            patchedWeaponAmmoFilters
        );
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // SPT's MongoId type typically does not ship with a System.Text.Json converter.
        // We provide one so rule files can use plain string TPLs.
        options.Converters.Add(new MongoIdJsonConverter());

        return options;
    }

    private static List<WeaponCompatRule> LoadRules(string modRoot, ILogger log, SalcosArsenalMod.Settings settings)
    {
        var rules = new List<WeaponCompatRule>();
        var folder = Path.Combine(modRoot, "config", "compat", "weapons");

        if (!Directory.Exists(folder))
        {
            return rules;
        }

        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(folder, "*.jsonc", SearchOption.AllDirectories))
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var file in files)
        {
            try
            {
                var json = File.ReadAllText(file);
                var rule = JsonSerializer.Deserialize<WeaponCompatRule>(json, JsonOptions);
                if (rule is null || string.IsNullOrWhiteSpace(rule.WeaponTpl.ToString()))
                {
                    log.LogWarning("[SalcosArsenal] WeaponsCompat: invalid rule in '{File}'. Missing weaponTpl. Skipped.", Path.GetFileName(file));
                    continue;
                }

                // Rule-level strict overrides global strict.
                rule.Strict = rule.Strict ?? settings.StrictMode;

                rules.Add(rule);
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "[SalcosArsenal] WeaponsCompat: failed to read '{File}'.", Path.GetFileName(file));
                if (settings.StrictMode)
                {
                    throw;
                }
            }
        }

        return rules;
    }

    private static Dictionary<string, List<MongoId>> BuildAmmoByCaliberIndex(Dictionary<MongoId, TemplateItem> items)
    {
        var index = new Dictionary<string, List<MongoId>>(StringComparer.OrdinalIgnoreCase);

        foreach (var kvp in items)
        {
            var item = kvp.Value;
            var caliber = item.Properties?.Caliber;
            if (string.IsNullOrWhiteSpace(caliber))
            {
                continue;
            }

            if (!index.TryGetValue(caliber, out var list))
            {
                list = new List<MongoId>();
                index[caliber] = list;
            }

            list.Add(item.Id);
        }

        return index;
    }

    private static int PatchWeaponAmmoFilters(
        TemplateItem weapon,
        List<MongoId> ammoTplsForCaliber,
        HashSet<MongoId>? excludeAmmoTpls
    )
    {
        var added = 0;

        // Patch chambers
        added += PatchSlotsFilterSet(weapon.Properties?.Chambers, ammoTplsForCaliber, excludeAmmoTpls);

        // Patch internal magazines / cartridge lists on weapons
        added += PatchSlotsFilterSet(weapon.Properties?.Cartridges, ammoTplsForCaliber, excludeAmmoTpls);

        return added;
    }

    private static int PatchWeaponMagazineSlot(
        TemplateItem weapon,
        string desiredCaliber,
        HashSet<MongoId>? excludeMagazineTpls,
        Dictionary<MongoId, TemplateItem> items
    )
    {
        if (weapon.Properties?.Slots is null)
        {
            return 0;
        }

        var magSlot = weapon.Properties.Slots.FirstOrDefault(s => string.Equals(s.Name, "mod_magazine", StringComparison.OrdinalIgnoreCase));
        if (magSlot?.Properties?.Filters is null)
        {
            return 0;
        }

        var targetFilter = magSlot.Properties.Filters.FirstOrDefault();
        if (targetFilter?.Filter is null)
        {
            return 0;
        }

        // Build candidate mags: any magazine whose cartridges slot can accept ammo of desired caliber.
        var candidateMagTpls = new List<MongoId>();
        foreach (var kvp in items)
        {
            var candidate = kvp.Value;
            if (candidate.Properties?.Cartridges is null)
            {
                continue;
            }

            if (!MagazineSupportsCaliber(candidate, desiredCaliber, items))
            {
                continue;
            }

            candidateMagTpls.Add(candidate.Id);
        }

        var added = 0;
        foreach (var magTpl in candidateMagTpls)
        {
            if (excludeMagazineTpls is not null && excludeMagazineTpls.Contains(magTpl))
            {
                continue;
            }

            if (targetFilter.Filter.Add(magTpl))
            {
                added++;
            }
        }

        return added;
    }

    private static bool MagazineSupportsCaliber(TemplateItem magazine, string caliber, Dictionary<MongoId, TemplateItem> items)
    {
        var cartridges = magazine.Properties?.Cartridges;
        if (cartridges is null)
        {
            return false;
        }

        foreach (var slot in cartridges)
        {
            var filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter;
            if (filter is null)
            {
                continue;
            }

            foreach (var ammoTpl in filter)
            {
                if (!items.TryGetValue(ammoTpl, out var ammo) || ammo.Properties is null)
                {
                    continue;
                }

                if (string.Equals(ammo.Properties.Caliber, caliber, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int PatchSlotsFilterSet(IEnumerable<Slot>? slots, List<MongoId> toAdd, HashSet<MongoId>? exclude)
    {
        if (slots is null)
        {
            return 0;
        }

        var added = 0;

        foreach (var slot in slots)
        {
            var filter = slot.Properties?.Filters?.FirstOrDefault()?.Filter;
            if (filter is null)
            {
                continue;
            }

            foreach (var tpl in toAdd)
            {
                if (exclude is not null && exclude.Contains(tpl))
                {
                    continue;
                }

                if (filter.Add(tpl))
                {
                    added++;
                }
            }
        }

        return added;
    }

    private sealed class WeaponCompatRule
    {
        public MongoId WeaponTpl { get; set; }
        public string? DisplayName { get; set; }

        // WTT-aligned fields
        public string? CaliberOverride { get; set; } // maps to weapon._props.ammoCaliber

        // Behavior flags
        public bool AllowAmmoByCaliber { get; set; } = true;
        public bool AllowMagazinesByCaliber { get; set; } = true;

        // Explicit exclusions
        public HashSet<MongoId>? ExcludeAmmoTpls { get; set; }
        public HashSet<MongoId>? ExcludeMagazineTpls { get; set; }

        // Rule-level strictness override
        public bool? Strict { get; set; }
    }

    private sealed class MongoIdJsonConverter : JsonConverter<MongoId>
    {
        private static readonly Func<string, MongoId> Factory = CreateFactory();

        public override MongoId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String)
            {
                throw new JsonException("MongoId must be a JSON string.");
            }

            var value = reader.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new JsonException("MongoId string is null/empty.");
            }

            return Factory(value);
        }

        public override void Write(Utf8JsonWriter writer, MongoId value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToString());
        }

        private static Func<string, MongoId> CreateFactory()
        {
            var t = typeof(MongoId);

            var ctor = t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, binder: null, [typeof(string)], modifiers: null);
            if (ctor is not null)
            {
                return s => (MongoId)ctor.Invoke([s]);
            }

            var parse = t.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static, binder: null, [typeof(string)], modifiers: null);
            if (parse is not null)
            {
                return s => (MongoId)parse.Invoke(null, [s])!;
            }

            // Last resort: try implicit operator from string
            var op = t.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => (m.Name == "op_Implicit" || m.Name == "op_Explicit")
                                     && m.ReturnType == t
                                     && m.GetParameters().Length == 1
                                     && m.GetParameters()[0].ParameterType == typeof(string));

            if (op is not null)
            {
                return s => (MongoId)op.Invoke(null, [s])!;
            }

            throw new InvalidOperationException("No MongoId(string) constructor/Parse/implicit operator found.");
        }
    }
}
