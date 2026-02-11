using Microsoft.Extensions.Logging;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal;

public static class AmmoCompatModule
{
    private static bool _applied;

    public static void Apply(DatabaseService databaseService, SalcosArsenalMod.Settings settings, ILogger log)
    {
        if (_applied)
        {
            if (settings.Debug)
            {
                log.LogInformation("[SalcosArsenal] AmmoCompat already applied. Skipping.");
            }

            return;
        }

        _applied = true;

        var items = databaseService.GetItems();
        var ammoByCaliber = BuildAmmoByCaliberIndex(items);

        var patchedWeapons = 0;
        var patchedWeaponEntries = 0;
        var patchedMagazines = 0;
        var patchedMagazineEntries = 0;

        foreach (var kvp in items)
        {
            var item = kvp.Value;
            var props = item.Properties;
            if (props is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(props.AmmoCaliber))
            {
                if (ammoByCaliber.TryGetValue(props.AmmoCaliber, out var ammoTpls))
                {
                    var added = 0;
                    added += PatchSlotsFilterSet(props.Chambers, ammoTpls);
                    added += PatchSlotsFilterSet(props.Cartridges, ammoTpls);

                    if (added > 0)
                    {
                        patchedWeapons++;
                        patchedWeaponEntries += added;
                    }
                }

                continue;
            }

            if (props.Cartridges is null)
            {
                continue;
            }

            var magazineCaliber = TryResolveSingleMagazineCaliber(item, items);
            if (string.IsNullOrWhiteSpace(magazineCaliber))
            {
                continue;
            }

            if (!ammoByCaliber.TryGetValue(magazineCaliber, out var magAmmoTpls))
            {
                continue;
            }

            var magAdded = PatchSlotsFilterSet(props.Cartridges, magAmmoTpls);
            if (magAdded > 0)
            {
                patchedMagazines++;
                patchedMagazineEntries += magAdded;
            }
        }

        if (settings.Debug)
        {
            log.LogInformation(
                "[SalcosArsenal] AmmoCompat applied. WeaponsPatched: {Weapons} (+{WeaponEntries} ammo entries), MagazinesPatched: {Mags} (+{MagEntries} ammo entries)",
                patchedWeapons,
                patchedWeaponEntries,
                patchedMagazines,
                patchedMagazineEntries
            );
        }
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

    private static int PatchSlotsFilterSet(IEnumerable<Slot>? slots, List<MongoId> ammoTpls)
    {
        if (slots is null)
        {
            return 0;
        }

        var added = 0;

        foreach (var slot in slots)
        {
            if (slot?.Properties?.Filters is null)
            {
                continue;
            }

            var filter = slot.Properties.Filters.FirstOrDefault();
            if (filter?.Filter is null)
            {
                continue;
            }

            var before = filter.Filter.Count;

            var existing = new HashSet<MongoId>(filter.Filter);
            foreach (var tpl in ammoTpls)
            {
                if (existing.Add(tpl))
                {
                    filter.Filter.Add(tpl);
                }
            }

            added += (filter.Filter.Count - before);
        }

        return added;
    }

    private static string? TryResolveSingleMagazineCaliber(TemplateItem magazine, Dictionary<MongoId, TemplateItem> items)
    {
        var cartridges = magazine.Properties?.Cartridges;
        if (cartridges is null)
        {
            return null;
        }

        var firstSlot = cartridges.FirstOrDefault();
        var firstFilter = firstSlot?.Properties?.Filters?.FirstOrDefault();
        var firstAmmoTpl = firstFilter?.Filter?.FirstOrDefault();
        if (firstAmmoTpl is null)
        {
            return null;
        }

        if (!items.TryGetValue(firstAmmoTpl.Value, out var ammoItem))
        {
            return null;
        }

        return ammoItem.Properties?.Caliber;
    }
}
