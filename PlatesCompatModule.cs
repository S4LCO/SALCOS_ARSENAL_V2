using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Logging;
using SPTarkov.Server.Core.Services;

namespace SalcosArsenal;

public static class PlatesCompatModule
{
    private static bool _applied;

    private static readonly HashSet<string> HardPlateSlotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "front_plate",
        "back_plate",
        "left_side_plate",
        "right_side_plate",
    };

    private static readonly HashSet<string> SoftInsertSlotNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "soft_armor_front",
        "soft_armor_back",
        "soft_armor_left",
        "soft_armor_right",
        "soft_insert",
        "soft_inserts",
    };

    public static void Apply(DatabaseService databaseService, SalcosArsenalMod.Settings settings, ILogger log)
    {
        if (_applied)
        {
            if (settings.Debug)
                log.LogInformation("[SalcosArsenal] PlatesCompat already applied. Skipping.");

            return;
        }

        _applied = true;

        var itemsDictObj = databaseService.GetTables().Templates.Items;
        if (itemsDictObj is not IDictionary items)
            return;

        var armorPlateBase = TryGetWttBaseClassValue("ARMOR_PLATE");
        var armorInsertBase = TryGetWttBaseClassValue("ARMOR_INSERT");

        var hardPlateTpls = CollectByParent(items, armorPlateBase);
        var softInsertTpls = CollectSoftInserts(items, armorInsertBase);

        var patchedItems = 0;
        var patchedSlots = 0;
        var added = 0;

        foreach (DictionaryEntry entry in items)
        {
            var tplId = entry.Key?.ToString() ?? string.Empty;
            var item = entry.Value;
            if (item == null)
                continue;

            try
            {
                var props = GetMemberValue(item, "Properties") ?? GetMemberValue(item, "_props");
                if (props == null)
                    continue;

                var slotsObj = GetMemberValue(props, "Slots") ?? GetMemberValue(props, "slots");
                if (slotsObj is not IEnumerable slots)
                    continue;

                var itemPatched = false;

                foreach (var slot in slots)
                {
                    if (slot == null)
                        continue;

                    var slotName = (GetMemberValue(slot, "Name") ?? GetMemberValue(slot, "_name"))?.ToString() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(slotName))
                        continue;

                    var isHardSlot = IsHardPlateSlot(slotName);
                    var isSoftSlot = IsSoftInsertSlot(slotName);

                    if (!isHardSlot && !isSoftSlot)
                        continue;

                    var slotProps = GetMemberValue(slot, "Properties") ?? GetMemberValue(slot, "_props");
                    if (slotProps == null)
                        continue;

                    var filtersObj = GetMemberValue(slotProps, "Filters") ?? GetMemberValue(slotProps, "filters");
                    if (filtersObj is not IEnumerable filtersEnum)
                        continue;

                    var firstFilter = filtersEnum.Cast<object?>().FirstOrDefault();
                    if (firstFilter == null)
                        continue;

                    var filterListObj = GetMemberValue(firstFilter, "Filter") ?? GetMemberValue(firstFilter, "filter");
                    if (filterListObj == null)
                        continue;

                    var filterList = EnsureStringList(firstFilter, filterListObj, "Filter")
                                  ?? EnsureStringList(firstFilter, filterListObj, "filter");

                    if (filterList == null)
                        continue;

                    var before = filterList.Count;

                    if (isHardSlot && hardPlateTpls.Count > 0)
                        AddUnique(filterList, hardPlateTpls);

                    if (isSoftSlot && softInsertTpls.Count > 0)
                        AddUnique(filterList, softInsertTpls);

                    var delta = filterList.Count - before;
                    if (delta > 0)
                    {
                        itemPatched = true;
                        patchedSlots++;
                        added += delta;
                    }
                }

                if (itemPatched)
                    patchedItems++;
            }
            catch (Exception ex)
            {
                if (settings.StrictMode)
                    throw;

                log.LogWarning(ex, "[SalcosArsenal] PlatesCompat failed for item {Tpl}", tplId);
            }
        }

        // Release policy:
        // - Debug=true: always show summary
        // - Debug=false: only log if we actually changed something
        if (settings.Debug || added > 0)
        {
            log.LogInformation(
                "[SalcosArsenal] PlatesCompat applied. HardPlates={Hard} SoftInserts={Soft} ItemsPatched={Items} SlotsPatched={Slots} EntriesAdded={Added}",
                hardPlateTpls.Count,
                softInsertTpls.Count,
                patchedItems,
                patchedSlots,
                added
            );
        }
    }

    private static bool IsHardPlateSlot(string slotName)
    {
        if (HardPlateSlotNames.Contains(slotName))
            return true;

        var n = slotName.ToLowerInvariant();
        if (!n.Contains("plate"))
            return false;

        if (n.Contains("soft"))
            return false;

        return true;
    }

    private static bool IsSoftInsertSlot(string slotName)
    {
        if (SoftInsertSlotNames.Contains(slotName))
            return true;

        var n = slotName.ToLowerInvariant();
        var isSoft = n.Contains("soft");
        var isArmorOrInsert = n.Contains("armor") || n.Contains("insert");
        return isSoft && isArmorOrInsert;
    }

    private static HashSet<string> CollectByParent(IDictionary items, string? parentId)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(parentId))
            return set;

        foreach (DictionaryEntry entry in items)
        {
            var item = entry.Value;
            if (item == null)
                continue;

            var parent = (GetMemberValue(item, "Parent") ?? GetMemberValue(item, "ParentId"))?.ToString();
            if (string.Equals(parent, parentId, StringComparison.OrdinalIgnoreCase))
            {
                var id = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    set.Add(id);
            }
        }

        return set;
    }

    private static HashSet<string> CollectSoftInserts(IDictionary items, string? insertParentId)
    {
        var byParent = CollectByParent(items, insertParentId);
        if (byParent.Count > 0)
            return byParent;

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (DictionaryEntry entry in items)
        {
            var item = entry.Value;
            if (item == null)
                continue;

            var name = (GetMemberValue(item, "Name") ?? GetMemberValue(item, "_name"))?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            var lower = name.ToLowerInvariant();
            var isSoft = lower.Contains("soft");
            var isArmorOrInsert = lower.Contains("armor") || lower.Contains("insert");

            if (isSoft && isArmorOrInsert)
            {
                var id = entry.Key?.ToString();
                if (!string.IsNullOrWhiteSpace(id))
                    set.Add(id);
            }
        }

        return set;
    }

    private static void AddUnique(IList<string> target, IEnumerable<string> toAdd)
    {
        var existing = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);

        foreach (var tpl in toAdd)
        {
            if (existing.Add(tpl))
                target.Add(tpl);
        }
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

    private static IList<string>? EnsureStringList(object parent, object listObj, string memberName)
    {
        if (listObj is IList<string> ok)
            return ok;

        if (listObj is IList nonGeneric)
        {
            var converted = new List<string>(nonGeneric.Count);
            foreach (var x in nonGeneric)
            {
                if (x == null)
                    continue;

                var s = x.ToString();
                if (!string.IsNullOrWhiteSpace(s))
                    converted.Add(s);
            }

            TrySetMemberValue(parent, memberName, converted);
            return converted;
        }

        return null;
    }

    private static void TrySetMemberValue(object obj, string name, object value)
    {
        var t = obj.GetType();

        var prop = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(obj, value);
            return;
        }

        var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(obj, value);
        }
    }

    private static string? TryGetWttBaseClassValue(string fieldName)
    {
        try
        {
            var t = Type.GetType("WTTServerCommonLib.Constants.BaseClasses, WTT-ServerCommonLib", throwOnError: false);
            if (t == null)
                return null;

            var field = t.GetField(fieldName, BindingFlags.Public | BindingFlags.Static);
            return field?.GetValue(null)?.ToString();
        }
        catch
        {
            return null;
        }
    }
}
