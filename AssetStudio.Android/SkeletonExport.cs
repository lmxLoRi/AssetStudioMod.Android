using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AssetStudio;
using Object = AssetStudio.Object;

namespace AssetStudioMobile
{
    /// <summary>
    /// Collects skeleton animations -- Spine and DragonBones -- into one folder per model.
    ///
    /// There is nothing to copy here from the desktop, because the desktop has no Spine support at
    /// all: no extractor, no grouping, no mention of the word. It writes the parts by name and the
    /// extension work is what made them readable; the files arrive already complete.
    ///
    /// So this is not a parser. A skeleton's parts are separate assets that already contain
    /// everything, and what is missing is that they are scattered: the atlas describes the pages, the
    /// skeleton names the atlas, the texture holds the picture, and they sit in the output next to
    /// thousands of unrelated files. Grouping them is the whole job.
    ///
    /// Two naming schemes, both from what the games call things:
    ///     Spine        "model.atlas"  "model.json"  "model.png"    (the atlas names its page)
    ///     DragonBones  "model_ske.json"  "model_tex.json"  "model_tex.png"
    /// </summary>
    internal static class SkeletonExport
    {
        private sealed class Model
        {
            public string Base;
            public bool DragonBones;
            public TextAsset Skeleton;
            public string SkeletonName;
            public TextAsset Atlas;
            public string AtlasName;
            public readonly List<string> TextureNames = new List<string>();
        }

        public static int Export(AssetsManager manager,
                                 IReadOnlyDictionary<Object, string> containers,
                                 string outputRoot,
                                 ExportOptions options,
                                 Action<string> log)
        {
            var models = Find(manager, containers, explain: true);
            if (models.Count == 0)
            {
                log("没有找到 Spine 或 DragonBones 骨骼动画");
                return 0;
            }

            log($"找到 {models.Count} 个骨骼动画（Spine {models.Values.Count(m => !m.DragonBones)} 个，" +
                $"DragonBones {models.Values.Count(m => m.DragonBones)} 个）");

            var textures = Textures(manager);

            var written = 0;
            foreach (var model in models.Values.OrderBy(m => m.Base, StringComparer.Ordinal))
            {
                try
                {
                    var folder = Path.Combine(outputRoot, model.DragonBones ? "DragonBones" : "Spine",
                                              Safe(model.Base));
                    Directory.CreateDirectory(folder);

                    if (model.Skeleton != null) Write(folder, model.SkeletonName, model.Skeleton.m_Script);
                    if (model.Atlas != null) Write(folder, model.AtlasName, model.Atlas.m_Script);

                    var missing = 0;
                    var pages = new List<string>();

                    foreach (var name in model.TextureNames)
                    {
                        if (!textures.TryGetValue(name, out var texture)) { missing++; continue; }

                        var dest = Path.Combine(folder, name + ".png");
                        var plan = Extractor.Plan(texture, options, containers);
                        if (plan == null) { missing++; continue; }

                        plan.Write(texture, dest, options);
                        pages.Add(name);
                    }

                    // The viewer will not open a Spine model until its own profile exists beside it.
                    // Writing it means the folder can be used as exported.
                    if (!model.DragonBones && pages.Count > 0 && model.Skeleton != null && model.Atlas != null)
                        WriteProfile(folder, model, pages);

                    written++;

                    // Read one batch at a time, so a model whose parts are spread over more than one
                    // bundle arrives in pieces. Said out loud, because half a model written in
                    // silence looks like a model.
                    var incomplete = new List<string>();
                    if (model.Skeleton == null) incomplete.Add("骨架");
                    if (model.Atlas == null) incomplete.Add("图集");
                    if (missing > 0) incomplete.Add($"{missing} 个贴图");

                    if (incomplete.Count > 0)
                        log($"{model.Base}：这批里缺 {string.Join("、", incomplete)}（可能跨 bundle）");
                }
                catch (Exception ex)
                {
                    log($"骨骼动画导出失败（{model.Base}）：{ex.GetType().Name}: {ex.Message}");
                }
            }

            log($"骨骼动画：已导出 {written} 个模型到 {outputRoot}");
            return written;
        }

        /// <summary>
        /// The names an asset might be known by, best first.
        ///
        /// Both are needed, because the two families keep their extension in different places. A
        /// Spine part carries it in its own name ("M095_Spine.atlas"); a DragonBones part does not
        /// ("M098_Model_ske") and is published with it (".../M098_Model_ske.json"). Classifying by
        /// either one alone found half a tree and reported the other half as empty -- 28 Spine and
        /// no DragonBones, then 42 DragonBones and no Spine.
        /// </summary>
        private static List<string> LogicalNames(TextAsset asset, IReadOnlyDictionary<Object, string> containers)
        {
            var names = new List<string>(2);
            if (!string.IsNullOrEmpty(asset.m_Name)) names.Add(asset.m_Name);

            if (containers != null && containers.TryGetValue(asset, out var container)
                && !string.IsNullOrEmpty(container))
            {
                var file = Path.GetFileName(container);
                if (!string.IsNullOrEmpty(file) && !names.Contains(file)) names.Add(file);
            }

            // And each of those with its extension taken off.
            //
            // A Spine skeleton is published either with no extension at all, or as ".skel", or as
            // "_ske.json"/"_tex.json"; the atlas is ".atlas". The model's name is what is left when
            // that comes off, and it is the name the two sides have to agree on. The matching rules
            // knew ".json" and nothing else, so every skeleton published as "X.skel" failed to attach
            // to the atlas "X.atlas" -- and X.atlas was written with its pages and no skeleton. That
            // is 176 of 429 models on the cache this was found on, and the survey had already reported
            // the two as a pair, because it matches on content.
            foreach (var name in names.ToList())
            {
                var stripped = BaseOf(name);
                if (!string.IsNullOrEmpty(stripped) && !names.Contains(stripped)) names.Add(stripped);
            }

            return names;
        }

        /// <summary>
        /// How many skeletons and atlases a loaded set actually holds, for the export's own log.
        ///
        /// The question it answers is which side is failing: if a group was loaded with its skeleton's
        /// bundle and the set still holds no skeleton, the bundle was never in the group; if it holds
        /// one and no model came out of it, the assembly is what failed.
        /// </summary>
        public static string CountParts(AssetsManager manager, IReadOnlyDictionary<Object, string> containers)
        {
            var skeletons = 0;
            var atlases = 0;

            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is TextAsset asset) || asset.m_Script == null || asset.m_Script.Length == 0)
                        continue;

                    if (LooksLikeAtlas(asset.m_Script)) atlases++;
                    else if (LooksLikeSkeleton(asset.m_Script)) skeletons++;
                }
            }

            var texts = 0;
            foreach (var file in manager.AssetsFileList)
                foreach (var obj in file.Objects)
                    if (obj is TextAsset) texts++;

            return $"图集 {atlases}、骨架 {skeletons}、文本资产共 {texts}、文件 {manager.AssetsFileList.Count}";
        }

        /// <summary>
        /// What this batch holds, and which of it the assembly failed to take.
        ///
        /// Instrumentation, not a change to how anything works: the export keeps pairing the way it
        /// always has, and this only says what that pairing left on the floor. The interesting number
        /// is how many skeletons were read and attached to nothing -- each one is a model that will be
        /// written without an animation, and the names on the line are what a working pairing has to
        /// go on instead.
        /// </summary>
        public static List<string> Explain(AssetsManager manager, IReadOnlyDictionary<Object, string> containers)
        {
            var lines = new List<string>();

            var models = Find(manager, containers);
            var texts = new List<TextAsset>();
            foreach (var file in manager.AssetsFileList)
                foreach (var obj in file.Objects)
                    if (obj is TextAsset t && t.m_Script != null && t.m_Script.Length > 0) texts.Add(t);

            var usedSkeletons = new HashSet<TextAsset>();
            var usedAtlases = new HashSet<TextAsset>();
            foreach (var model in models.Values)
            {
                if (model.Skeleton != null) usedSkeletons.Add(model.Skeleton);
                if (model.Atlas != null) usedAtlases.Add(model.Atlas);
            }

            foreach (var asset in texts)
            {
                var names = LogicalNames(asset, containers);
                var first = names.Count > 0 ? names[0] : "(无名)";
                var published = PublishedName(asset, containers) ?? "-";

                if (usedAtlases.Contains(asset))
                {
                    lines.Add($"图集   {first}  | 发布名 {published}  | 已建模型");
                }
                else if (usedSkeletons.Contains(asset))
                {
                    lines.Add($"骨架   {first}  | 发布名 {published}  | 已配上一个模型");
                }
                else if (LooksLikeSkeleton(asset.m_Script))
                {
                    lines.Add($"骨架   {first}  | 发布名 {published}  | **没配上任何模型**");
                }
                else if (LooksLikeAtlas(asset.m_Script))
                {
                    lines.Add($"图集   {first}  | 发布名 {published}  | **没建出模型**");
                }
            }

            return lines;
        }

        /// <summary>Content, because the names of a Spine skeleton do not say what it is.</summary>
        private static bool LooksLikeSkeleton(byte[] bytes)
        {
            var head = Head(bytes);
            return head.Contains("\"skeleton\"") || head.Contains("\"armature\"");
        }

        private static bool LooksLikeAtlas(byte[] bytes)
        {
            var head = Head(bytes);
            if (head.Contains('{')) return head.Contains("\"SubTexture\"");
            return head.Contains("bounds:") && head.Contains("size:");
        }

        private static string Head(byte[] bytes)
        {
            try { return System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 4096)); }
            catch { return string.Empty; }
        }

        /// <summary>Every texture the manager holds, by name -- a model names its pages.</summary>
        private static Dictionary<string, Texture2D> Textures(AssetsManager manager)
        {
            var textures = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is Texture2D texture && !string.IsNullOrEmpty(texture.m_Name))
                        textures[texture.m_Name] = texture;
                }
            }
            return textures;
        }

        /// <summary>
        /// What models these objects describe, and which bundles hold each one.
        ///
        /// The first half of a two-phase export. It answers "what is here and where" without keeping
        /// any of it, so the caller can read the tree one batch at a time and clear each batch -- and
        /// because the answer is keyed by the model's own base name, a model whose parts are spread
        /// over several bundles is still one entry once the batches are merged.
        ///
        /// The second half loads only the bundles named here, one model at a time, and runs the
        /// ordinary export over them. Peak memory is one model instead of one tree.
        /// </summary>
        public static IEnumerable<(string Key, List<string> Sources)> Discover(
            AssetsManager manager, IReadOnlyDictionary<Object, string> containers)
        {
            var textures = Textures(manager);

            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (!(obj is TextAsset asset) || asset.m_Script == null || asset.m_Script.Length == 0)
                        continue;

                    if (!(asset.m_Script is byte[] bytes)) continue;
                    var isAtlas = LooksLikeAtlas(bytes);
                    if (!isAtlas && !LooksLikeSkeleton(bytes)) continue;

                    // The asset's own name, not the name its bundle publishes it under. A skeleton
                    // and its atlas share the first (both are the model's name) and often do not share
                    // the second -- Enemy_8017's parts are published as
                    // "Enemy_FulfillingStage006_8018_4.prefab", which is why keying on the published
                    // name left them in two groups and the atlas without its skeleton.
                    var name = asset.m_Name;
                    if (string.IsNullOrEmpty(name)) name = LogicalNames(asset, containers).FirstOrDefault();
                    if (string.IsNullOrEmpty(name)) continue;

                    var sources = new List<string> { Extractor.SourcePath(file) };

                    if (isAtlas)
                    {
                        foreach (var page in PageNames(bytes))
                        {
                            if (textures.TryGetValue(Path.GetFileNameWithoutExtension(page), out var texture))
                                sources.Add(Extractor.SourcePath(texture.assetsFile));
                        }
                    }

                    sources.RemoveAll(string.IsNullOrEmpty);
                    if (sources.Count == 0) continue;

                    // Filed under the model's own name, not under whatever this batch assembled.
                    //
                    // A model's parts do not have to share a bundle, and on this cache they do not:
                    // Enemy_8017's atlas and its skeleton are published under the same container path
                    // but live in different bundles. Pairing inside one batch therefore paired nothing
                    // for those models -- and the export wrote them with an atlas, its pages, and no
                    // skeleton, which was 176 of 429. Both parts carry the model's name, so filing them
                    // by that name is enough for the merge to bring them back together.
                    yield return ($"{(LooksLikeDragon(name, bytes) ? "db" : "spine")}:{BaseOf(name)}", sources);
                }
            }
        }

        /// <summary>File names, not model names: strip whatever extension either family kept.</summary>
        private static string BaseOf(string name)
        {
            foreach (var suffix in new[]
                     {
                         ".atlas.txt", ".atlas", "_ske.json", "_tex.json", ".json", ".skel", ".asset",
                         "_ske", "_tex",
                     })
            {
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return name.Substring(0, name.Length - suffix.Length);
            }
            return Path.GetFileNameWithoutExtension(name);
        }

        private static bool LooksLikeDragon(string name, byte[] bytes)
        {
            if (name.EndsWith("_ske", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.EndsWith("_tex", StringComparison.OrdinalIgnoreCase)) return true;
            try
            {
                var head = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(bytes.Length, 2048));
                return head.Contains("\"armature\"") || head.Contains("\"SubTexture\"");
            }
            catch { return false; }
        }

        /// Groups the assets by the model their names say they belong to.
        ///
        /// Two passes, and the order is the whole point. A model is created by its atlas -- or its
        /// "_ske"/"_tex" pair -- and a skeleton can only be attached to a model that already exists.
        /// One pass therefore lost every skeleton that came before its atlas in the file order:
        /// 8 of 28 models had a skeleton and 20 had none, and which was which depended on nothing
        /// but where the assets happened to sit in the bundles.
        /// </summary>
        private static Dictionary<string, Model> Find(AssetsManager manager,
                                                      IReadOnlyDictionary<Object, string> containers,
                                                      bool explain = false)
        {
            var models = new Dictionary<string, Model>(StringComparer.Ordinal);
            var textAssets = new List<TextAsset>();

            foreach (var file in manager.AssetsFileList)
            {
                foreach (var obj in file.Objects)
                {
                    if (obj is TextAsset asset && !string.IsNullOrEmpty(asset.m_Name)) textAssets.Add(asset);
                }
            }

            // Pass one: what defines a model.
            foreach (var asset in textAssets)
            {
                foreach (var candidate in LogicalNames(asset, containers))
                {
                    if (Anchor(candidate, asset, models)) break;
                }
            }

            // Pass two: what belongs to one.
            foreach (var asset in textAssets)
            {
                foreach (var candidate in LogicalNames(asset, containers))
                {
                    if (Attach(candidate, asset, containers, models)) break;
                }
            }

            if (explain) Explain2(textAssets, models);
            return models;
        }

        /// <summary>Creates a model from the asset that defines it.</summary>
        private static bool Anchor(string name, TextAsset asset, Dictionary<string, Model> models)
        {
            Model ModelFor(string baseName, bool dragonBones)
            {
                if (!models.TryGetValue(baseName, out var model))
                {
                    model = new Model { Base = baseName, DragonBones = dragonBones };
                    models[baseName] = model;
                }
                return model;
            }

            // DragonBones first: its names also end in ".json".
            if (name.EndsWith("_ske.json", StringComparison.OrdinalIgnoreCase))
            {
                var model = ModelFor(name.Substring(0, name.Length - "_ske.json".Length), true);
                model.Skeleton = asset;
                model.SkeletonName = name;
                return true;
            }

            if (name.EndsWith("_tex.json", StringComparison.OrdinalIgnoreCase))
            {
                var model = ModelFor(name.Substring(0, name.Length - "_tex.json".Length), true);
                model.Atlas = asset;
                model.AtlasName = name;
                model.TextureNames.Add(model.Base + "_tex");
                return true;
            }

            foreach (var suffix in new[] { ".atlas.txt", ".atlas" })
            {
                if (!name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;

                var model = ModelFor(name.Substring(0, name.Length - suffix.Length), false);
                model.Atlas = asset;
                model.AtlasName = name;

                // Every page, not just the first. A character atlas is several pages, and a model
                // whose second page is missing cannot be opened by anything -- the big models here
                // have up to four, and only the first was being written.
                foreach (var page in PageNames(asset.m_Script))
                    model.TextureNames.Add(Path.GetFileNameWithoutExtension(page));
                return true;
            }

            return false;
        }

        /// <summary>Attaches an asset to the model that already exists for it.</summary>
        private static bool Attach(string name, TextAsset asset,
                                   IReadOnlyDictionary<Object, string> containers,
                                   Dictionary<string, Model> models)
        {
            // A plain ".json" is a skeleton for the model of that name, if an atlas made one.
            if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                var stem = name.Substring(0, name.Length - ".json".Length);
                if (!models.TryGetValue(stem, out var model) || model.Skeleton != null) return false;

                model.Skeleton = asset;
                model.SkeletonName = name;
                return true;
            }

            // A spine-unity skeleton is often named with no extension anywhere: m_Name is
            // "M098_Spine" and the container is ".../M098_Spine_SkeletonData.asset", while the
            // content is the skeleton. Its own base name is the model it belongs to.
            var bare = name;
            if (models.TryGetValue(bare, out var owner) && owner.Skeleton == null)
            {
                owner.Skeleton = asset;

                // The asset's own name, with the extension the container publishes it under -- the
                // same pair an ordinary TextAsset export produces, so the two agree.
                //
                // The container's whole file name is not used: it carries the game's internal
                // "_SkeletonData" suffix, which is a name nothing else refers to and not the one the
                // viewer's profile dialog shows.
                var extension = Path.GetExtension(PublishedName(asset, containers));
                owner.SkeletonName = string.IsNullOrEmpty(extension) ? bare + ".json" : bare + extension;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Writes what one assembly saw and what it made of it.
        ///
        /// To a file. The question is narrow now -- the survey already brings a model's parts together
        /// and the export still writes 176 of 429 without a skeleton -- so this says whether the loaded
        /// set held a skeleton at all, and which names the two sides offered each other when it did.
        /// </summary>
        private static void Explain2(List<TextAsset> textAssets, Dictionary<string, Model> models)
        {
            try
            {
                var path = System.IO.Path.Combine(
                    Android.App.Application.Context.GetExternalFilesDir(null)?.AbsolutePath
                        ?? Android.App.Application.Context.FilesDir.AbsolutePath,
                    "find-report.txt");

                var lines = new List<string>
                {
                    $"=== 载入集：文本资产 {textAssets.Count} → 模型 {models.Count}" +
                    $"（有骨架 {models.Values.Count(m => m.Skeleton != null)}）===",
                };

                foreach (var asset in textAssets.Take(8))
                    lines.Add($"    文本资产: {asset.m_Name}  脚本 {asset.m_Script?.Length ?? -1} 字节");

                foreach (var model in models.Values.Where(m => m.Skeleton == null).Take(8))
                    lines.Add($"    没配上: 模型 {model.Base}，图集 m_Name={model.Atlas?.m_Name}");

                System.IO.File.AppendAllLines(path, lines);
            }
            catch { }
        }

        /// <summary>The file name the bundle publishes an asset under, or null.</summary>
        private static string PublishedName(TextAsset asset, IReadOnlyDictionary<Object, string> containers)
        {
            if (containers == null || asset == null) return null;
            if (!containers.TryGetValue(asset, out var container) || string.IsNullOrEmpty(container)) return null;

            return Path.GetFileName(container);
        }

        private static readonly string[] PageProperties = { "size:", "format:", "filter:", "repeat:", "pma:" };
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".webp" };

        /// <summary>
        /// The page image names an atlas declares.
        ///
        /// A page block begins with its image file name and continues with that page's properties,
        /// while a region block begins with a region name and continues with bounds and offsets. So
        /// a line is a page name when the line after it is a page property -- or, for a page with no
        /// properties at all, when it is an image file name.
        /// </summary>
        private static List<string> PageNames(byte[] bytes)
        {
            var pages = new List<string>();
            if (bytes == null || bytes.Length == 0) return pages;

            string text;
            try { text = System.Text.Encoding.UTF8.GetString(bytes); }
            catch { return pages; }

            var lines = text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.Contains(':')) continue;

                var next = i + 1 < lines.Length ? lines[i + 1].Trim() : string.Empty;
                var isPage = PageProperties.Any(p => next.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                          || ImageExtensions.Any(e => line.EndsWith(e, StringComparison.OrdinalIgnoreCase));

                if (isPage && !pages.Contains(line)) pages.Add(line);
            }

            return pages;
        }

        /// <summary>
        /// Writes the viewer's own profile beside the model.
        ///
        /// The files alone are not enough -- Live2DViewerEX will not open a Spine model until one of
        /// these exists, which is what its "create profile" dialog produces. A model exported without
        /// it has to be opened, pointed at three or four files and saved by hand before it can be
        /// seen at all, per model. The shape is small: a type, the skeleton's file name, and for the
        /// atlas its file and its pages.
        ///
        /// Only for Spine. The viewer has no DragonBones runtime at all, so there is nothing a
        /// profile could do for those.
        /// </summary>
        private static void WriteProfile(string folder, Model model, List<string> pages)
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
            json.AppendLine($"  \"skeleton\": {Quote(model.SkeletonName)},");
            json.AppendLine("  \"atlases\": [");
            json.AppendLine("    {");
            json.AppendLine($"      \"atlas\": {Quote(model.AtlasName)},");
            json.AppendLine($"      \"tex_names\": [ {string.Join(", ", pages.Select(Quote))} ],");
            json.AppendLine($"      \"textures\": [ {string.Join(", ", pages.Select(p => Quote(p + ".png")))} ]");
            json.AppendLine("    }");
            json.AppendLine("  ]");
            json.AppendLine("}");

            File.WriteAllText(Path.Combine(folder, model.Base + ".config.json"), json.ToString());
        }

        /// <summary>A JSON string, quoted, for a file name.</summary>
        private static string Quote(string value)
        {
            var escaped = (value ?? string.Empty)
                .Replace("\\", "\\\\").Replace("\"", "\\\"")
                .Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

            return "\"" + escaped + "\"";
        }

        private static void Write(string folder, string name, byte[] bytes)
            => File.WriteAllBytes(Path.Combine(folder, Safe(name)), bytes);

        private static string Safe(string name)
        {
            if (string.IsNullOrEmpty(name)) return "unnamed";

            var builder = new System.Text.StringBuilder(name.Length);
            foreach (var c in name)
                builder.Append(c < 0x20 || c is '/' or '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|' ? '_' : c);

            return builder.ToString();
        }
    }
}
