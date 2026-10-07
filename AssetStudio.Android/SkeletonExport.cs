using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Object = AssetStudio.Object;

namespace AssetStudioMobile
{
    /// <summary>
    /// Spine and DragonBones models, found once and then written from what was found.
    ///
    /// The finding is separated from the writing on purpose. A model's parts are separate assets, they
    /// do not all live in one bundle, and -- the part that cost the most to learn -- what an asset is
    /// cannot be told from its name: a game publishes the same atlas text under two container names, a
    /// Spine skeleton with no extension at all, and a DragonBones skeleton as "_ske.json". So the
    /// survey decides, by content, which asset is the skeleton, which is the atlas and which textures
    /// are its pages, and hands those decisions over as asset references. The writer loads the bundles
    /// and writes exactly what it was given. Nothing decides a second time, and nothing looks at an
    /// extension.
    /// </summary>
    internal static class SkeletonExport
    {
        /// <summary>
        /// Where an asset lives: the bundle it was read from, and its id within it. Path ids are
        /// unique inside one file, so the pair identifies an asset without holding on to it.
        /// </summary>
        public readonly struct Ref
        {
            public readonly string Bundle;
            public readonly long PathID;

            public Ref(string bundle, long pathID)
            {
                Bundle = bundle;
                PathID = pathID;
            }

            public bool IsSet => !string.IsNullOrEmpty(Bundle);
        }

        /// <summary>One model, as the survey found it: exactly which assets to write, and under what names.</summary>
        public sealed class Plan
        {
            public string Key;
            public string Base;
            public bool DragonBones;

            public Ref Skeleton;
            public string SkeletonName;

            public Ref Atlas;
            public string AtlasName;

            /// <summary>The atlas's pages, in the order the atlas declares them.</summary>
            public readonly List<string> Pages = new List<string>();

            /// <summary>Each page's texture, filled in by the caller from the survey's texture map.</summary>
            public readonly Dictionary<string, Ref> PageRefs = new Dictionary<string, Ref>(StringComparer.Ordinal);

            /// <summary>The bundles this model needs loaded.</summary>
            public readonly HashSet<string> Sources = new HashSet<string>(StringComparer.Ordinal);
        }

        private static string Trim(string name, string suffix)
            => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) && name.Length > suffix.Length
                ? name.Substring(0, name.Length - suffix.Length)
                : null;

        private static string Head(byte[] bytes, int limit)
        {
            try { return System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, limit)); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// A Spine atlas, by what it holds: page lines with size and bounds, and no JSON braces.
        /// A DragonBones atlas is JSON, and holds SubTextures.
        /// </summary>
        private static bool LooksLikeAtlas(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            var head = Head(bytes, 4096);
            if (head.Contains('{')) return head.Contains("\"SubTexture\"");
            return head.Contains("bounds:") && head.Contains("size:");
        }

        /// <summary>A skeleton, by what it holds: Spine declares a skeleton, DragonBones an armature.</summary>
        private static bool LooksLikeSkeleton(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return false;

            var head = Head(bytes, 4096);
            return head.Contains("\"skeleton\"") || head.Contains("\"armature\"");
        }

        private static bool IsDragonBones(string name, byte[] bytes)
        {
            if (name.EndsWith("_ske", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_tex", StringComparison.OrdinalIgnoreCase)) return true;

            var head = Head(bytes, 2048);
            return head.Contains("\"armature\"") || head.Contains("\"SubTexture\"");
        }

        /// <summary>
        /// The name the model goes by: the asset's own name with whatever the two families append to
        /// it taken off. It is what the skeleton and the atlas have in common, and nothing else is.
        /// </summary>
        private static string BaseOf(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;

            // The suffixes these games put on a part's name. "_skel" is the one that mattered: the
            // skeleton of "2nd Anniversary Login" is published as "2nd Anniversary Login_skel", and
            // stripping only "_ske" left it filed under a name of its own, unpaired, and the model was
            // reported -- wrongly -- as having no skeleton in the tree at all.
            foreach (var suffix in new[]
                     {
                         ".atlas.txt", ".atlas", "_ske.json", "_tex.json", ".json", ".skel", ".asset",
                         "_skel", "_skeleton", "_skeledata", "_atlas", "_texture",
                         "_ske", "_tex",
                     })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }

            return Path.GetFileNameWithoutExtension(name);
        }

        /// <summary>The file name the bundle publishes an asset under, or null.</summary>
        private static string PublishedName(TextAsset asset, IReadOnlyDictionary<Object, string> containers)
            => containers != null && containers.TryGetValue(asset, out var container) ? container : null;

        /// <summary>
        /// The name to write an asset under: its own, plus the extension the bundle publishes it with.
        ///
        /// The container's whole file name is not used. It carries the game's own suffixes -- a Spine
        /// skeleton that is called "M095_Spine" is published under "..._SkeletonData.asset" -- which
        /// nothing else refers to and which the viewer's own profile dialog does not show.
        /// </summary>
        private static string WrittenName(string name)
        {
            // As told: a name that carries its own extension is written as it is, and one without an
            // extension gets ".asset". The container's extension is deliberately not used -- it is the
            // game's internal name for the bundle entry, and taking it is what put an atlas's bytes
            // into a file called ".prefab".
            return Path.HasExtension(name) ? name : name + ".asset";
        }

        private static readonly string[] PageProperties = { "size:", "format:", "filter:", "repeat:", "pma:" };
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

        /// <summary>The texture a DragonBones atlas names, which it calls "imagePath".</summary>
        private static List<string> DragonPages(byte[] bytes)
        {
            var pages = new List<string>();
            if (bytes == null || bytes.Length == 0) return pages;

            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(
                         Head(bytes, 1 << 20), "\"imagePath\"\\s*:\\s*\"([^\"]+)\""))
            {
                var name = Path.GetFileNameWithoutExtension(match.Groups[1].Value);
                if (!string.IsNullOrEmpty(name) && !pages.Contains(name)) pages.Add(name);
            }

            return pages;
        }

        /// <summary>
        /// Every page an atlas declares, in order.
        ///
        /// All of them, not just the first: a character atlas is several pages and a model whose second
        /// page is missing cannot be opened by anything. The big atlases here have up to four.
        /// </summary>
        private static List<string> PageNames(byte[] bytes)
        {
            var pages = new List<string>();
            if (bytes == null || bytes.Length == 0) return pages;

            var lines = Head(bytes, 1 << 20).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.Contains(':')) continue;

                var next = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
                if (PageProperties.Any(x => next.StartsWith(x, StringComparison.OrdinalIgnoreCase))
                    || ImageExtensions.Any(x => line.EndsWith(x, StringComparison.OrdinalIgnoreCase)))
                {
                    var name = Path.GetFileNameWithoutExtension(line);
                    if (!pages.Contains(name)) pages.Add(name);
                }
            }

            return pages;
        }

        /// <summary>
        /// Reads the tree and says what is in it: one plan per part, decided by content.
        ///
        /// A part is filed under the name of the model it belongs to, so the caller can merge the
        /// batches and end up with one model per name however many bundles its parts were spread over.
        /// Atlas and skeleton are told apart by what they hold, never by what they are called -- a game
        /// publishes the same atlas text twice, once as "X.atlas" and once as "X.prefab", and calling
        /// the second one a skeleton is what wrote 211 of 429 models an atlas in place of a skeleton.
        /// </summary>
        public static IEnumerable<Plan> Discover(AssetsManager manager,
                                                 IReadOnlyDictionary<Object, string> containers)
        {
            foreach (var file in manager.AssetsFileList)
            {
                var bundle = Extractor.SourcePath(file);

                foreach (var obj in file.Objects)
                {
                    if (!(obj is TextAsset asset) || asset.m_Script == null || asset.m_Script.Length == 0)
                        continue;

                    var name = asset.m_Name;
                    if (string.IsNullOrEmpty(name)) continue;

                    // By name, and by the exact suffixes the two families use. The atlas is "<x>.atlas"
                    // and the skeleton is "<x>.skel", so both reduce to "<x>" and pair with each other.
                    //
                    // Nothing else is looked at. In particular "_Atlas" and "_SkeletonData" are
                    // MonoBehaviours -- the game's own AtlasAsset and SkeletonDataAsset -- and are not
                    // TextAssets at all, so they are never seen here; and there is exactly one
                    // "<x>.atlas", so a model cannot pick up a second copy of its own atlas.
                    string baseName;
                    bool dragon;
                    bool isAtlas;

                    if (name.EndsWith(".atlas", StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = name.Substring(0, name.Length - ".atlas".Length);
                        dragon = false;
                        isAtlas = true;
                    }
                    else if (name.EndsWith(".skel", StringComparison.OrdinalIgnoreCase))
                    {
                        baseName = name.Substring(0, name.Length - ".skel".Length);
                        dragon = false;
                        isAtlas = false;
                    }
                    else if (Trim(name, "_tex.json") != null || Trim(name, "_tex") != null)
                    {
                        // DragonBones names its two halves "X_tex" and "X_ske". Whether the game also
                        // appends ".json" varies, and both forms have to be accepted -- taking only
                        // "_tex.json" sent "_tex" down the extension-less branch, where it became a
                        // skeleton under a name of its own and met nothing.
                        baseName = Trim(name, "_tex.json") ?? Trim(name, "_tex");
                        dragon = true;
                        isAtlas = true;
                    }
                    else if (Trim(name, "_ske.json") != null || Trim(name, "_ske") != null)
                    {
                        baseName = Trim(name, "_ske.json") ?? Trim(name, "_ske");
                        dragon = true;
                        isAtlas = false;
                    }
                    else if (!Path.HasExtension(name))
                    {
                        // A skeleton whose name carries no extension at all: the asset is simply called
                        // "Character_24" beside the atlas "Character_24.atlas". This is the common case
                        // here, and accepting only ".skel" left every one of them unfound -- which is
                        // why models kept being reported as having no skeleton in the tree at all.
                        //
                        // On its own such an asset is not a model: the writer only writes a model that
                        // has an atlas. So an ordinary extension-less TextAsset -- a script, a table --
                        // never turns into a folder of its own.
                        baseName = name;
                        dragon = false;
                        isAtlas = false;
                    }
                    else
                    {
                        continue;
                    }

                    if (string.IsNullOrEmpty(baseName)) continue;

                    var plan = new Plan
                    {
                        Key = $"{(dragon ? "db" : "spine")}:{baseName}",
                        Base = Safe(baseName),
                        DragonBones = dragon,
                    };

                    var reference = new Ref(bundle, asset.m_PathID);

                    if (isAtlas)
                    {
                        plan.Atlas = reference;
                        plan.AtlasName = WrittenName(name);

                        // The pages the atlas itself names, which is where "x", "x_2", "x_3" come from.
                        // A DragonBones atlas is JSON and names its one texture in "imagePath" instead.
                        if (dragon)
                        {
                            foreach (var page in DragonPages(asset.m_Script)) plan.Pages.Add(page);
                            if (plan.Pages.Count == 0) plan.Pages.Add(baseName + "_tex");
                        }
                        else
                        {
                            foreach (var page in PageNames(asset.m_Script)) plan.Pages.Add(page);
                        }
                    }
                    else
                    {
                        plan.Skeleton = reference;
                        plan.SkeletonName = WrittenName(name);
                    }

                    if (!string.IsNullOrEmpty(bundle)) plan.Sources.Add(bundle);
                    yield return plan;
                }
            }
        }

        /// <summary>The asset a reference points at, in a manager that has had its bundles loaded.</summary>
        private static T Resolve<T>(AssetsManager manager, Ref reference) where T : Object
        {
            if (!reference.IsSet) return null;

            // Matched on the path id, not on the path. A cache file does not report the same path every
            // time it is read -- the survey recorded "__data" for these textures and the export's
            // reloaded copy reports something else -- so an exact-path match found nothing even though
            // the asset was sitting in the loaded set. A path id is unique inside its file, and a file
            // of the same name is preferred when two files happen to share one.
            var wanted = Path.GetFileName(reference.Bundle);
            T found = null;

            foreach (var file in manager.AssetsFileList)
            {
                var path = Extractor.SourcePath(file);
                var samePath = string.Equals(path, reference.Bundle, StringComparison.Ordinal);
                var sameName = string.Equals(Path.GetFileName(path), wanted, StringComparison.OrdinalIgnoreCase);

                foreach (var obj in file.Objects)
                {
                    if (obj.m_PathID != reference.PathID || !(obj is T typed)) continue;

                    if (samePath) return typed;
                    if (found == null || sameName) found = typed;
                }
            }

            return found;
        }

        /// <summary>
        /// Writes one model from its plan: the assets the survey named, and nothing else.
        ///
        /// No matching happens here. Whatever the survey decided an asset is, it stays; an atlas is
        /// never promoted to a skeleton, and a model that has no skeleton is written without one and
        /// without a profile claiming it has one.
        /// </summary>
        public static int Write(AssetsManager manager,
                                IReadOnlyDictionary<Object, string> containers,
                                string outputRoot,
                                ExportOptions options,
                                Action<string> log,
                                Plan plan)
        {
            var folder = Path.Combine(outputRoot, plan.DragonBones ? "DragonBones" : "Spine",
                                      Safe(plan.Base));
            Directory.CreateDirectory(folder);

            var skeleton = Resolve<TextAsset>(manager, plan.Skeleton);
            var atlas = Resolve<TextAsset>(manager, plan.Atlas);

            if (skeleton != null) Write(folder, plan.SkeletonName, skeleton.m_Script);
            if (atlas != null) Write(folder, plan.AtlasName, atlas.m_Script);

            var pages = new List<string>();
            var diagnostic = (string)null;
            var noName = 0;      // the page name never made it into the texture table
            var noAsset = 0;     // it is in the table, but the bundle holding it was not loaded
            var noPlan = 0;      // the asset was found, but nothing could be written from it

            foreach (var page in plan.Pages)
            {
                if (!plan.PageRefs.TryGetValue(page, out var reference)) { noName++; continue; }

                var texture = Resolve<Texture2D>(manager, reference);
                if (texture == null)
                {
                    noAsset++;

                    if (noAsset == 1)
                    {
                        // Which half is failing: the bundle never got loaded, or it was loaded and the
                        // path recorded for it no longer matches. Counting the same path id across
                        // every loaded file, ignoring paths, tells the two apart.
                        var sameId = 0;
                        var sameBundle = 0;

                        foreach (var f in manager.AssetsFileList)
                        {
                            if (string.Equals(Extractor.SourcePath(f), reference.Bundle, StringComparison.Ordinal))
                                sameBundle++;

                            foreach (var o in f.Objects)
                                if (o.m_PathID == reference.PathID) sameId++;
                        }

                        diagnostic = $"{page}: 记录的 bundle={Path.GetFileName(reference.Bundle)}" +
                                     $"，被加载 {sameBundle} 个文件，同 pathID 的资产 {sameId} 个";
                    }

                    continue;
                }

                var export = Extractor.Plan(texture, options, containers);
                if (export == null) { noPlan++; continue; }

                export.Write(texture, Path.Combine(folder, page + ".png"), options);
                pages.Add(page);
            }

            var missing = noName + noAsset + noPlan;

            // The viewer will not open a Spine model until its own profile exists beside it, and it
            // needs all three parts to make sense of it.
            if (!plan.DragonBones && skeleton != null && atlas != null && pages.Count > 0)
                WriteProfile(folder, plan, pages);

            // Said out loud rather than written in silence: a folder with an atlas and no skeleton
            // looks like a model until something tries to use it.
            var incomplete = new List<string>();
            if (skeleton == null) incomplete.Add("骨架");
            if (atlas == null) incomplete.Add("图集");
            if (missing > 0) incomplete.Add($"{missing} 个贴图");

            if (incomplete.Count > 0)
                log($"{plan.Base}：缺 {string.Join("、", incomplete)}" +
                    (missing > 0 ? $"（表里无名字 {noName} / bundle 里无资产 {noAsset} / 无导出方案 {noPlan}）" : "") +
                    (diagnostic != null ? $" [{diagnostic}]" : ""));

            return 1;
        }

        private static void WriteProfile(string folder, Plan plan, List<string> pages)
        {
            var json = new System.Text.StringBuilder();

            json.AppendLine("{");
            json.AppendLine("  \"conf_ver\": 1,");
            json.AppendLine("  \"type\": 9,");
            json.AppendLine("  \"controllers\": {");
            json.AppendLine("    \"param_hit\": {},");
            json.AppendLine("    \"param_loop\": {},");
            json.AppendLine("    \"key_trigger\": {},");
            json.AppendLine("    \"area_trigger\": {},");
            json.AppendLine("    \"eye_blink\": { \"min_interval\": 500, \"max_interval\": 6000 },");
            json.AppendLine("    \"lip_sync\": { \"gain\": 5.0 },");
            json.AppendLine("    \"mouse_tracking\": { \"smooth_time\": 0.15 },");
            json.AppendLine("    \"auto_breath\": {},");
            json.AppendLine("    \"extra_motion\": {},");
            json.AppendLine("    \"accelerometer\": {},");
            json.AppendLine("    \"intimacy_system\": {},");
            json.AppendLine("    \"battery\": {},");
            json.AppendLine("    \"slot_opacity\": {},");
            json.AppendLine("    \"slot_color\": {}");
            json.AppendLine("  },");
            json.AppendLine("  \"options\": { \"tex_type\": 0, \"edge_padding\": false, \"shader_type\": 1 },");
            json.AppendLine($"  \"skeleton\": {Quote(plan.SkeletonName)},");
            json.AppendLine("  \"atlases\": [");
            json.AppendLine("    {");
            json.AppendLine($"      \"atlas\": {Quote(plan.AtlasName)},");
            json.AppendLine($"      \"tex_names\": [ {string.Join(", ", pages.Select(Quote))} ],");
            json.AppendLine($"      \"textures\": [ {string.Join(", ", pages.Select(p => Quote(p + ".png")))} ]");
            json.AppendLine("    }");
            json.AppendLine("  ]");
            json.AppendLine("}");

            File.WriteAllText(Path.Combine(folder, plan.Base + ".config.json"), json.ToString());
        }

        /// <summary>A JSON string, quoted, for a file name.</summary>
        private static string Quote(string value)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (var c in value)
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        private static void Write(string folder, string name, byte[] bytes)
        {
            if (string.IsNullOrEmpty(name) || bytes == null) return;
            File.WriteAllBytes(Path.Combine(folder, name), bytes);
        }

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";

            var sb = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
            {
                if (c == '/' || c == '\\' || c == ':' || c == '*' || c == '?' || c == '"'
                    || c == '<' || c == '>' || c == '|' || c < ' ')
                {
                    sb.Append('_');
                }
                else
                {
                    sb.Append(c);
                }
            }

            var safe = sb.ToString().Trim().TrimEnd('.');
            return safe.Length == 0 ? "unnamed" : safe;
        }

        /// <summary>
        /// Every texture the manager holds, by name: an atlas names its pages, and a page may be in a
        /// bundle no part of the model otherwise points at.
        /// </summary>
        public static Dictionary<string, Ref> TextureRefs(AssetsManager manager)
        {
            var textures = new Dictionary<string, Ref>(StringComparer.Ordinal);

            foreach (var file in manager.AssetsFileList)
            {
                var bundle = Extractor.SourcePath(file);
                foreach (var obj in file.Objects)
                {
                    if (obj is Texture2D texture && !string.IsNullOrEmpty(texture.m_Name))
                        textures[texture.m_Name] = new Ref(bundle, texture.m_PathID);
                }
            }

            return textures;
        }
    }
}
