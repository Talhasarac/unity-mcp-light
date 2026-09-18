using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MCPForUnity.Editor.Helpers;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace MCPForUnity.Editor.Tools.Prefabs
{
    public static partial class InspectPrefab
    {
        private const int MaxPathsPerFile = 5;

        /// <summary>
        /// mode=usages: every prefab and scene under Assets/ that uses a script (script=ClassName) or an asset
        /// (asset=Assets/...). Candidates are narrowed with AssetDatabase.GetDependencies (direct dependencies,
        /// followed through nested prefabs and other assets with a memo); only direct users are loaded, to list
        /// the object paths inside them. Scenes are listed by file; paths only for scenes that are already open.
        /// </summary>
        private static string RunUsages(ToolParams p, int maxChars)
        {
            string script = p.Get("script");
            string assetArg = p.Get("asset");
            if (string.IsNullOrWhiteSpace(script) && string.IsNullOrWhiteSpace(assetArg))
                throw new InspectError("usages needs script= (class name) or asset= (Assets/... path).");

            Type type = null;
            string targetPath;
            string what;
            if (!string.IsNullOrWhiteSpace(script))
            {
                MonoScript ms = FindMonoScript(script.Trim());
                type = ms.GetClass();
                targetPath = AssetDatabase.GetAssetPath(ms);
                what = $"{(type != null ? type.Name : ms.name)} ({targetPath})";
            }
            else
            {
                targetPath = AssetPathUtility.SanitizeAssetPath(assetArg.Trim());
                if (string.IsNullOrEmpty(targetPath) || string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(targetPath)) ||
                    AssetDatabase.LoadMainAssetAtPath(targetPath) == null)
                {
                    string stem = Path.GetFileNameWithoutExtension(targetPath ?? assetArg);
                    var similar = AssetDatabase.FindAssets(stem).Select(AssetDatabase.GUIDToAssetPath).Take(200);
                    throw new InspectError($"No asset at '{assetArg}'." + Suggest(Nearest(targetPath ?? assetArg, similar)));
                }
                what = targetPath;
            }

            bool dll = targetPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
            string[] folders = { "Assets" };
            var prefabs = AssetDatabase.FindAssets("t:Prefab", folders).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            var scenes = AssetDatabase.FindAssets("t:Scene", folders).Select(AssetDatabase.GUIDToAssetPath).Distinct().ToList();
            var dataAssets = new List<string>();
            if (type != null && typeof(ScriptableObject).IsAssignableFrom(type))
            {
                dataAssets = AssetDatabase.FindAssets("t:" + type.Name, folders).Select(AssetDatabase.GUIDToAssetPath)
                    .Where(a => !a.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)).Distinct().ToList();
            }

            var finder = new DependencyFinder(targetPath);
            var direct = new List<string>();
            var via = new List<(string file, string through)>();
            foreach (string file in prefabs.Concat(scenes).Concat(dataAssets))
            {
                if (file == targetPath) continue;
                string how = finder.Via(file);
                if (how == null) continue;
                if (how.Length == 0) direct.Add(file);
                else via.Add((file, how));
            }

            // Every class in a DLL shares the DLL path, so confirm DLL-script prefab users by their components.
            if (dll && type != null)
            {
                direct = direct.Where(f => !f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ||
                                           (AssetDatabase.LoadAssetAtPath<GameObject>(f)?.GetComponentsInChildren(type, true).Length ?? 0) > 0).ToList();
            }

            int prefabCount = direct.Concat(via.Select(v => v.file)).Count(f => f.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            int sceneCount = direct.Concat(via.Select(v => v.file)).Count(f => f.EndsWith(".unity", StringComparison.OrdinalIgnoreCase));
            int otherCount = direct.Count + via.Count - prefabCount - sceneCount;
            string header = $"usages of {what}: {prefabCount} prefabs, {sceneCount} scenes" +
                            (otherCount > 0 ? $", {otherCount} assets" : "") +
                            $" ({direct.Count} direct, {via.Count} via nested/dependencies; searched {prefabs.Count} prefabs, {scenes.Count} scenes under Assets/)";
            if (direct.Count == 0 && via.Count == 0) return header + "\nnothing under Assets/ uses it.";

            var output = new TextOut(maxChars);
            int shown = 0;
            foreach (string file in direct.OrderBy(SortKey).ThenBy(f => f))
            {
                if (!output.Line($"{file}: {DescribeDirectUse(file, type, targetPath)}")) break;
                shown++;
            }
            if (!output.Full)
            {
                foreach (var (file, through) in via.OrderBy(v => SortKey(v.file)).ThenBy(v => v.file))
                {
                    string throughText = through.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? "@" + Path.GetFileName(through) : through;
                    if (!output.Line($"{file}: via {throughText}")) break;
                    shown++;
                }
            }

            string note = output.Full ? $"... truncated: {direct.Count + via.Count - shown} more files. Use a larger max_chars." : null;
            return output.Finish(header, note);
        }

        private static int SortKey(string file) =>
            file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? 0 :
            file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? 1 : 2;

        /// <summary>Finds the MonoScript for a class name (short or full), throwing with similar names.</summary>
        private static MonoScript FindMonoScript(string name)
        {
            MonoScript[] scripts = MonoImporter.GetAllRuntimeMonoScripts();
            var matches = scripts.Where(ms =>
            {
                Type c = ms != null ? ms.GetClass() : null;
                return c != null && (c.Name == name || c.FullName == name);
            }).ToList();
            if (matches.Count == 0)
                matches = scripts.Where(ms => ms != null && ms.GetClass() == null && ms.name == name).ToList();

            var classes = matches.Select(ms => ms.GetClass()).Where(c => c != null).Distinct().ToList();
            if (classes.Count > 1)
                throw new InspectError($"'{name}' is ambiguous: {string.Join(", ", classes.Select(c => c.FullName))}. Pass the full name.");
            if (matches.Count > 0) return matches[0];

            var names = scripts.Where(ms => ms != null).Select(ms => ms.GetClass()?.Name ?? ms.name);
            throw new InspectError($"No MonoBehaviour or ScriptableObject class '{name}'." + Suggest(Nearest(name, names)));
        }

        /// <summary>Object paths inside a directly-using file, e.g. "E36_CAR, Body/Doors/FL_Door (+2 in nested prefabs)".</summary>
        private static string DescribeDirectUse(string file, Type type, string targetPath)
        {
            List<GameObject> roots;
            if (file.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(file);
                if (asset == null) return "direct";
                roots = new List<GameObject> { asset };
            }
            else if (file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                Scene scene = SceneManager.GetSceneByPath(file);
                if (!scene.IsValid() || !scene.isLoaded) return "direct (scene not open; object paths not resolved)";
                roots = scene.GetRootGameObjects().ToList();
            }
            else
            {
                return "direct";
            }

            var paths = new List<string>();
            int nested = 0;
            bool prefabTarget = type == null && targetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase);
            foreach (GameObject root in roots)
            {
                if (prefabTarget)
                {
                    // A prefab is "used" as a nested instance: list the instance roots, not internal link fields.
                    Transform scope = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? null : root.transform;
                    foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                    {
                        if (t == root.transform && scope != null) continue;
                        if (!PrefabUtility.IsAnyPrefabInstanceRoot(t.gameObject) ||
                            PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(t.gameObject) != targetPath) continue;
                        string p = scope != null ? ShowPath(RelPath(t, scope), scope) : RelPath(t, null);
                        if (!paths.Contains(p)) paths.Add(p);
                    }
                    continue;
                }

                Transform rootT = file.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? null : root.transform;
                foreach (Component c in root.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || !Uses(c, type, targetPath, out string field)) continue;
                    GameObject nearest = PrefabUtility.GetNearestPrefabInstanceRoot(c.gameObject);
                    if (nearest != null && nearest != root && rootT != null)
                    {
                        nested++;
                        continue;
                    }
                    string path = rootT != null ? ShowPath(RelPath(c.transform, rootT), rootT) : RelPath(c.transform, null);
                    string entry = field != null ? $"{path} ({TypeNamer.Short(c.GetType())}).{field}" : path;
                    if (!paths.Contains(entry)) paths.Add(entry);
                }
            }

            if (paths.Count == 0 && nested == 0) return "direct";
            string list = string.Join(", ", paths.Take(MaxPathsPerFile));
            if (paths.Count > MaxPathsPerFile) list += $", +{paths.Count - MaxPathsPerFile} more";
            if (nested > 0) list += (list.Length > 0 ? " " : "") + $"(+{nested} in nested prefabs)";
            return list;
        }

        /// <summary>Whether a component is the script (type) or references the asset; field names the reference.</summary>
        private static bool Uses(Component c, Type type, string targetPath, out string field)
        {
            field = null;
            if (type != null) return type.IsInstanceOfType(c);

            using (var so = new SerializedObject(c))
            {
                SerializedProperty it = so.GetIterator();
                bool enter = true;
                while (it.Next(enter))
                {
                    enter = !IsPlainArray(it);
                    if (it.propertyType != SerializedPropertyType.ObjectReference) continue;
                    if (it.propertyPath == "m_CorrespondingSourceObject" || it.propertyPath == "m_PrefabInstance" ||
                        it.propertyPath == "m_PrefabAsset" || it.propertyPath == "m_GameObject" || it.propertyPath == "m_Script") continue;
                    Object obj = it.objectReferenceValue;
                    if (obj == null || AssetDatabase.GetAssetPath(obj) != targetPath) continue;
                    field = FieldPath(it.propertyPath);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Does a file use the target, and through what? "" = direct dependency, a path = through that
        /// dependency (nested prefab, material, ...), null = not at all. Memoized across all files.
        /// </summary>
        private sealed class DependencyFinder
        {
            private readonly string _target;
            private readonly Dictionary<string, string> _memo = new Dictionary<string, string>();

            public DependencyFinder(string target) { _target = target; }

            public string Via(string file)
            {
                if (_memo.TryGetValue(file, out string known)) return known;
                _memo[file] = null; // cycle guard
                string[] deps = AssetDatabase.GetDependencies(file, false);
                if (deps.Contains(_target)) return _memo[file] = "";
                foreach (string dep in deps)
                {
                    if (dep == file || !CanHaveDependencies(dep)) continue;
                    if (Via(dep) != null) return _memo[file] = dep;
                }
                return null;
            }

            private static bool CanHaveDependencies(string path)
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                switch (ext)
                {
                    case ".cs": case ".dll": case ".asmdef": case ".asmref": case ".png": case ".jpg": case ".jpeg":
                    case ".tga": case ".psd": case ".exr": case ".hdr": case ".wav": case ".mp3": case ".ogg":
                    case ".ttf": case ".otf": case ".txt": case ".json": case ".bytes": case ".hlsl": case ".cginc":
                        return false;
                    default:
                        return true;
                }
            }
        }
    }
}
