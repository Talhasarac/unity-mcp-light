using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int MaxModsPerObject = 8;

        /// <summary>
        /// mode=overrides: for a variant (and for every nested prefab instance inside the prefab) the source
        /// prefab, then changed properties (old -> new), added/removed components and added/removed objects.
        /// Default overrides (root name/position/rotation) are counted, not listed.
        /// </summary>
        private static string RunOverrides(ToolParams p, int maxChars)
        {
            using (Target target = Target.Open(p.Get("prefab_path"), loadContents: true))
            {
                Transform root = target.Root.transform;
                var types = new TypeNamer();
                types.AddHierarchy(root);
                var refs = new RefPrinter(root, types);

                string chain = target.IsScene ? "" : VariantChain(target.AssetPath);
                var instances = root.GetComponentsInChildren<Transform>(true)
                    .Select(t => t.gameObject)
                    .Where(PrefabUtility.IsOutermostPrefabInstanceRoot)
                    .ToList();

                string header = $"{target.Label} overrides{(chain.Length > 0 ? "  " + chain : "")}  " +
                                $"({instances.Count(i => i.transform != root)} nested prefab instances)";
                if (instances.Count == 0) return header + "\nnot a variant and contains no nested prefab instances.";

                var output = new TextOut(maxChars);
                var unchanged = new List<string>();
                int shown = 0;
                foreach (GameObject inst in instances)
                {
                    var section = OverrideSection(inst, root, refs, out bool hasChanges);
                    string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inst);
                    string sourceName = string.IsNullOrEmpty(source) ? "MISSING-PREFAB" : Path.GetFileName(source);
                    if (!hasChanges)
                    {
                        unchanged.Add(sourceName);
                        shown++;
                        continue;
                    }
                    bool complete = true;
                    foreach (string line in section)
                    {
                        if (!output.Line(line)) { complete = false; break; }
                    }
                    if (!complete) break;
                    shown++;
                }

                if (!output.Full && unchanged.Count > 0)
                {
                    string list = string.Join(", ", unchanged.GroupBy(u => u).Select(g => g.Count() == 1 ? "@" + g.Key : $"@{g.Key} x{g.Count()}").Take(10));
                    output.Line($"{unchanged.Count} nested instances without changes: {list}{(unchanged.Distinct().Count() > 10 ? ", ..." : "")}");
                }

                string note = output.Full
                    ? $"... truncated: {instances.Count - shown} more prefab instances not shown. Use inspect_prefab tree filter=@ to locate them, or a larger max_chars."
                    : null;
                return output.Finish(header, note);
            }
        }

        /// <summary>"variant of @B.prefab <- @A.prefab": the base chain of a prefab variant asset.</summary>
        private static string VariantChain(string assetPath)
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (asset == null || PrefabUtility.GetPrefabAssetType(asset) != PrefabAssetType.Variant) return "";
            var names = new List<string>();
            Object current = asset;
            for (int guard = 0; guard < 32; guard++)
            {
                Object source = PrefabUtility.GetCorrespondingObjectFromSource(current);
                if (source == null) break;
                names.Add("@" + Path.GetFileName(AssetDatabase.GetAssetPath(source)));
                current = source;
            }
            return names.Count == 0 ? "variant (base missing)" : "variant of " + string.Join(" <- ", names);
        }

        private static List<string> OverrideSection(GameObject inst, Transform root, RefPrinter refs, out bool hasChanges)
        {
            var lines = new List<string>();
            string instPath = ShowPath(RelPath(inst.transform, root), root);
            string source = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(inst);
            string sourceName = string.IsNullOrEmpty(source) ? "MISSING-PREFAB" : Path.GetFileName(source);

            // Map source-prefab objects to their counterparts in this instance.
            var map = new Dictionary<Object, Object>();
            foreach (Transform t in inst.GetComponentsInChildren<Transform>(true))
            {
                Object sg = PrefabUtility.GetCorrespondingObjectFromSource(t.gameObject);
                if (sg != null) map[sg] = t.gameObject;
                foreach (Component c in t.GetComponents<Component>())
                {
                    if (c == null) continue;
                    Object sc = PrefabUtility.GetCorrespondingObjectFromSource(c);
                    if (sc != null) map[sc] = c;
                }
            }

            PropertyModification[] mods = PrefabUtility.GetPropertyModifications(inst) ?? new PropertyModification[0];
            int defaultOverrides = 0;
            var byTarget = new List<(Object target, List<PropertyModification> mods)>();
            var index = new Dictionary<Object, int>();
            foreach (PropertyModification mod in mods)
            {
                if (PrefabUtility.IsDefaultOverride(mod))
                {
                    defaultOverrides++;
                    continue;
                }
                Object key = mod.target;
                if (key == null)
                {
                    byTarget.Add((null, new List<PropertyModification> { mod }));
                    continue;
                }
                if (!index.TryGetValue(key, out int i))
                {
                    index[key] = i = byTarget.Count;
                    byTarget.Add((key, new List<PropertyModification>()));
                }
                byTarget[i].mods.Add(mod);
            }

            var added = PrefabUtility.GetAddedComponents(inst);
            var removed = PrefabUtility.GetRemovedComponents(inst);
            var addedObjects = PrefabUtility.GetAddedGameObjects(inst);
            var removedObjects = RemovedObjectLines(inst, root);

            int propCount = byTarget.Sum(b => b.mods.Count);
            hasChanges = propCount > 0 || added.Count > 0 || removed.Count > 0 || addedObjects.Count > 0 || removedObjects.Count > 0;

            string where = inst.transform == root ? $"root ({sourceName} base)" : $"@{sourceName} at {instPath}";
            lines.Add($"{where}: {propCount} props on {byTarget.Count} objs, +{added.Count}/-{removed.Count} comps, " +
                      $"+{addedObjects.Count}/-{removedObjects.Count} objs{(defaultOverrides > 0 ? $" ({defaultOverrides} default overrides hidden)" : "")}");

            foreach (var (targetObj, list) in byTarget)
            {
                lines.Add("  " + DescribeModTarget(targetObj, map, root, refs));
                foreach (PropertyModification mod in list.Take(MaxModsPerObject))
                    lines.Add($"    {FieldPath(mod.propertyPath)}: {OldValue(mod, refs)} -> {NewValue(mod, refs)}");
                if (list.Count > MaxModsPerObject) lines.Add($"    ... +{list.Count - MaxModsPerObject} more");
            }

            foreach (AddedComponent a in added)
            {
                if (a.instanceComponent == null) continue;
                lines.Add($"  + {ShowPath(RelPath(a.instanceComponent.transform, root), root)} ({refs.Types.Name(a.instanceComponent.GetType())})");
            }
            foreach (RemovedComponent r in removed)
            {
                string owner = r.containingInstanceGameObject != null ? ShowPath(RelPath(r.containingInstanceGameObject.transform, root), root) : "?";
                string type = r.assetComponent != null ? TypeNamer.Short(r.assetComponent.GetType()) : "?";
                lines.Add($"  - {owner} ({type})");
            }
            foreach (AddedGameObject a in addedObjects)
            {
                if (a.instanceGameObject == null) continue;
                int count = a.instanceGameObject.GetComponentsInChildren<Transform>(true).Length;
                lines.Add($"  + {ShowPath(RelPath(a.instanceGameObject.transform, root), root)} (object{(count > 1 ? $" + {count - 1} children" : "")})");
            }
            lines.AddRange(removedObjects);
            return lines;
        }

        private static List<string> RemovedObjectLines(GameObject inst, Transform root)
        {
            var lines = new List<string>();
#if UNITY_2022_2_OR_NEWER
            foreach (RemovedGameObject r in PrefabUtility.GetRemovedGameObjects(inst))
            {
                string parent = r.parentOfRemovedGameObjectInInstance != null
                    ? ShowPath(RelPath(r.parentOfRemovedGameObjectInInstance.transform, root), root)
                    : "?";
                lines.Add($"  - {parent}/{(r.assetGameObject != null ? r.assetGameObject.name : "?")} (object)");
            }
#endif
            return lines;
        }

        private static string DescribeModTarget(Object sourceObj, Dictionary<Object, Object> map, Transform root, RefPrinter refs)
        {
            if (sourceObj == null) return "MISSING target";
            if (map.TryGetValue(sourceObj, out Object instanceObj) && instanceObj != null)
            {
                GameObject go = instanceObj as GameObject ?? ((Component)instanceObj).gameObject;
                string path = ShowPath(RelPath(go.transform, root), root);
                return instanceObj is Component c ? $"{path} ({refs.Types.Name(c.GetType())})" : $"{path} (GameObject)";
            }
            return $"{sourceObj.name} ({TypeNamer.Short(sourceObj.GetType())}, not in this prefab)";
        }

        private static string OldValue(PropertyModification mod, RefPrinter refs)
        {
            if (mod.target == null) return "?";
            using (var so = new SerializedObject(mod.target))
            {
                SerializedProperty sp = so.FindProperty(mod.propertyPath);
                if (sp == null) return "?";
                if (sp.propertyType == SerializedPropertyType.ObjectReference)
                {
                    Object old = sp.objectReferenceValue;
                    return old == null ? "null" : AssetOrName(old);
                }
                return sp.propertyType == SerializedPropertyType.Generic ? "{...}" : FormatValue(sp, refs);
            }
        }

        private static string NewValue(PropertyModification mod, RefPrinter refs)
        {
            if (mod.objectReference != null) return refs.Describe(mod.objectReference);
            string value = mod.value ?? "";
            if (mod.target == null) return value;
            using (var so = new SerializedObject(mod.target))
            {
                SerializedProperty sp = so.FindProperty(mod.propertyPath);
                if (sp == null) return value;
                switch (sp.propertyType)
                {
                    case SerializedPropertyType.ObjectReference: return "null";
                    case SerializedPropertyType.Boolean: return value == "0" ? "false" : value == "1" ? "true" : value;
                    case SerializedPropertyType.String: return Quote(value);
                    case SerializedPropertyType.Enum:
                        return int.TryParse(value, out int i) && i >= 0 && i < sp.enumNames.Length ? sp.enumNames[i] : value;
                    default: return value;
                }
            }
        }

        /// <summary>A reference living in the source prefab: its path in that asset, or the asset path.</summary>
        private static string AssetOrName(Object obj)
        {
            GameObject go = obj as GameObject ?? (obj as Component)?.gameObject;
            if (go != null)
            {
                string path = RelPath(go.transform, go.transform.root);
                string comp = obj is Component c && !(c is Transform) ? $" ({TypeNamer.Short(c.GetType())})" : "";
                return (path.Length == 0 ? go.name : path) + comp;
            }
            string assetPath = AssetDatabase.GetAssetPath(obj);
            return string.IsNullOrEmpty(assetPath) ? obj.name : assetPath;
        }
    }
}
