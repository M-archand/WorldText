using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Extensions;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Class;
using K4WorldTextSharedAPI;
using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;


namespace WorldText
{
    [MinimumApiVersion(205)]
    public partial class PluginWorldText : BasePlugin, IPluginConfig<PluginConfig>
    {
        public override string ModuleName => "World Text";
        public override string ModuleAuthor => "Marchand";
        public override string ModuleVersion => "1.0.2";
        public required PluginConfig Config { get; set; } = new PluginConfig();
        public static PluginCapability<IK4WorldTextSharedAPI> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");
        private bool _hasMenuManager;
        private Dictionary<int, List<int>> _currentTextByGroup = new();
        private static readonly string chatPrefix = $" {ChatColors.Purple}[{ChatColors.LightPurple}World-Text{ChatColors.Purple}]";
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            try
            {
                var _k4 = Capability_SharedAPI.Get();
            }
            catch (Exception)
            {
                Logger.LogError("You don't have K4-WorldText-API installed. It is required. Download it from https://github.com/M-archand/K4-WorldText-API/releases");
            }

            RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
            {
                DisplayConfiguredText();
                return HookResult.Continue;
            });

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                Server.NextWorldUpdate(() =>
                {
                    if (Config.EnableDatabase)
                        LoadWorldTextFromDb();
                    else
                        LoadWorldTextFromJson(mapName);
                });
            });

            RegisterListener<Listeners.OnMapEnd>(() =>
            {
                var checkAPI = Capability_SharedAPI.Get();
                if (checkAPI != null)
                {
                    foreach (var ids in _currentTextByGroup.Values)
                        foreach (var id in ids)
                            checkAPI.RemoveWorldText(id, false);
                }
                _currentTextByGroup.Clear();
            });

            // Check for CS2MenuManager installation
            try
            {
                var dummy = MenuManager.MenuTypesList;
                _hasMenuManager = true;
            }
            catch (Exception)
            {
                _hasMenuManager = false;
                Server.PrintToConsole("[World-Text] CS2MenuManager API not found! Move menu command has been disabled.");
            }

            Config.Reload();
        }

        public void OnConfigParsed(PluginConfig config)
        {
            Config = config;

            const int ExpectedVersion = 3;
            if (Config.Version < ExpectedVersion)
                Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", ExpectedVersion, Config.Version);

            if (Config.EnableDatabase)
            {
                InitializeDatabaseConnectionString();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await EnsureTablesAsync().ConfigureAwait(false);
                        Server.NextWorldUpdate(() => LoadWorldTextFromDb());
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error loading WorldText info from database. Please check your credentials.");
                        Server.NextWorldUpdate(() => LoadWorldTextFromJson());
                    }
                });
            }
            else
            {
                Server.NextWorldUpdate(() => LoadWorldTextFromJson());
            }

            AddCommand($"css_{Config.AddCommand}", "Add text in front of you", OnTextAdd);
            AddCommand($"css_{Config.RemoveCommand}", "Removes the closest group of text", OnTextRemove);
            AddCommand($"css_{Config.MoveCommand}", "Opens a menu to adjust the text location/angles", OnTextMove);
            AddCommand("css_importtext", "Imports any existing JSON text locations into the database", OnImportText);
            AddCommand("css_reloadtext", "Refresh the config and reload the text in the world.", OnRefreshText);
        }

        public override void Unload(bool hotReload)
        {
            var checkAPI = Capability_SharedAPI.Get();
            if (checkAPI != null)
            {
                foreach (var groupTextList in _currentTextByGroup.Values)
                {
                    groupTextList.ForEach(id => checkAPI.RemoveWorldText(id, false));
                }
            }
            _currentTextByGroup.Clear();
        }

        private void SaveWorldTextToFile(Vector location, QAngle rotation, int groupNumber)
        {
            var mapName = Server.MapName;
            var mapsDirectory = Path.Combine(ModuleDirectory, "maps");
            var path = Path.Combine(mapsDirectory, $"{mapName}.json");

            var worldTextData = new WorldTextData
            {
                GroupNumber = groupNumber,
                Location = location.ToString(),
                Rotation = rotation.ToString()
            };

            List<WorldTextData> data;
            if (File.Exists(path))
            {
                data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path)) ?? new List<WorldTextData>();
            }
            else
            {
                data = new List<WorldTextData>();
            }

            data.Add(worldTextData);

            File.WriteAllText(path, JsonSerializer.Serialize(data, jsonOptions));
        }

        private async Task SaveWorldTextToDb(string mapName, int group, Vector location, QAngle rotation)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            var loc = VecToStringInvariant(location);
            var ang = AngToStringInvariant(rotation);

            string sql = $@"
                    INSERT IGNORE INTO `{table}` (`MapName`,`GroupNumber`,`Location`,`Angle`)
                    VALUES (@m,@g,@loc,@ang);";

            await conn.ExecuteAsync(sql, new { m = mapName, g = group, loc, ang });
        }

        private async Task<bool> RemoveClosestDbText(string mapName, Vector playerPos, CCSPlayerController player)
        {
            try
            {
                var checkAPI = Capability_SharedAPI.Get();
                if (checkAPI is null)
                {
                    Server.NextFrame(() =>
                        player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}K4-WorldText-API missing.")
                    );
                    return false;
                }

                // Find nearest list
                int? targetMsgId = null;
                int targetGroup = -1;
                float bestDist = float.MaxValue;
                Vector? targetLoc = null;
                QAngle? targetAng = null;

                foreach (var kvp in _currentTextByGroup)
                {
                    int group = kvp.Key;
                    foreach (var msgId in kvp.Value)
                    {
                        var lines = checkAPI.GetWorldTextLineEntities(msgId);
                        var line0 = lines?.Count > 0 ? lines[0] : null;
                        if (line0?.AbsOrigin == null) continue;

                        var loc = line0.AbsOrigin;
                        float dx = loc.X - playerPos.X, dy = loc.Y - playerPos.Y, dz = loc.Z - playerPos.Z;
                        float dist = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);

                        if (dist < bestDist && dist <= 200.0f)
                        {
                            bestDist = dist;
                            targetMsgId = msgId;
                            targetGroup = group;
                            targetLoc = loc;
                            targetAng = line0.AbsRotation;
                        }
                    }
                }

                if (targetMsgId == null || targetGroup == -1 || targetLoc == null || targetAng == null)
                {
                    Server.NextFrame(() =>
                        player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}Move closer to the text you want to remove.")
                    );
                    return false;
                }

                // Remove from the world
                checkAPI.RemoveWorldText(targetMsgId.Value, false);
                if (_currentTextByGroup.TryGetValue(targetGroup, out var list))
                    list.Remove(targetMsgId.Value);

                // Delete the matching row from DB
                await DeleteWorldTextFromDb(mapName, targetGroup, targetLoc, targetAng);

                Server.NextFrame(() =>
                {
                    player.PrintToChat($"{chatPrefix} {ChatColors.Lime}Removed one placement from {ChatColors.White}Group {targetGroup} {ChatColors.Lime}on {ChatColors.White}{mapName}");
                    RefreshText();
                });

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "RemoveClosestDbText failed");
                return false;
            }
        }

        public void RemoveClosestJsonText(CCSPlayerController player, CommandInfo command)
        {
            var checkAPI = Capability_SharedAPI.Get();
            if (checkAPI is null) return;

            dynamic? target = null;
            int groupWithTarget = -1;

            foreach (var group in _currentTextByGroup)
            {
                var groupTextList = group.Value;
                float removeDistance = 200.0f;

                var closest = groupTextList
                    .SelectMany(id => checkAPI.GetWorldTextLineEntities(id)?.Select(entity => new { Id = id, Entity = entity }) ?? Enumerable.Empty<dynamic>())
                    .Where(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null && DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) < removeDistance)
                    .OrderBy(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null ? DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) : float.MaxValue)
                    .FirstOrDefault();

                if (closest != null)
                {
                    target = closest;
                    groupWithTarget = group.Key;
                    break;
                }
            }

            if (target is null)
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.Red}Move within 200 units of the text that you want to remove.");
                return;
            }

            checkAPI.RemoveWorldText(target.Id, false);

            if (groupWithTarget != -1)
            {
                _currentTextByGroup[groupWithTarget].Remove(target.Id);
            }

            var mapName = Server.MapName;
            var mapsDirectory = Path.Combine(ModuleDirectory, "maps");
            var path = Path.Combine(mapsDirectory, $"{mapName}.json");

            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
                if (data != null)
                {
                    Vector entityVector = target.Entity.AbsOrigin;
                    data.RemoveAll(x =>
                    {
                        Vector location = ParseVector(x.Location);
                        return location.X == entityVector.X &&
                            location.Y == entityVector.Y &&
                            x.Rotation == target.Entity.AbsRotation.ToString();
                    });

                    string jsonString = JsonSerializer.Serialize(data, jsonOptions);
                    File.WriteAllText(path, jsonString);
                }
            }

            player.PrintToChat($"{chatPrefix} {ChatColors.Lime}Removed one placement from {ChatColors.White}Group {groupWithTarget} {ChatColors.Lime}on {ChatColors.White}{mapName}");
        }

        private void LoadWorldTextFromJson(string? passedMapName = null)
        {
            var mapName = passedMapName ?? Server.MapName;
            var mapsDirectory = Path.Combine(ModuleDirectory, "maps");
            var path = Path.Combine(mapsDirectory, $"{mapName}.json");

            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
                if (data == null) return;

                Task.Run(() =>
                {
                    foreach (var worldTextData in data)
                    {
                        var linesList = GetTextLines(worldTextData.GroupNumber);

                        Server.NextWorldUpdate(() =>
                        {
                            var checkAPI = Capability_SharedAPI.Get();
                            if (checkAPI != null && !string.IsNullOrEmpty(worldTextData.Location) && !string.IsNullOrEmpty(worldTextData.Rotation))
                            {
                                var messageID = checkAPI.AddWorldText(TextPlacement.Wall, linesList, ParseVector(worldTextData.Location), ParseQAngle(worldTextData.Rotation));
                                if (!_currentTextByGroup.ContainsKey(worldTextData.GroupNumber))
                                {
                                    _currentTextByGroup[worldTextData.GroupNumber] = new List<int>();
                                }
                                _currentTextByGroup[worldTextData.GroupNumber].Add(messageID);

                            }
                        });
                    }
                });
            }
        }

        private void LoadWorldTextFromDb()
        {
            var mapName = Server.MapName;
            Task.Run(async () =>
            {
                try
                {
                    string table = $"{Config.DatabaseSettings.TableName}";
                    using var conn = CreateDbConnection();

                    var rows = await conn.QueryAsync<WtTextRecord>(
                        $@"SELECT `Id`, `GroupNumber`, `Location`, `Angle`
                        FROM `{table}` WHERE `MapName`=@m;",
                        new { m = mapName });

                    Server.NextWorldUpdate(() =>
                    {
                        try
                        {
                            var api = Capability_SharedAPI.Get();
                            if (api is null) return;

                            foreach (var rec in rows)
                            {
                                var linesList = GetTextLines(rec.GroupNumber);

                                var loc = ParseVector(rec.Location);
                                var rot = ParseQAngle(rec.Angle);

                                var id = api.AddWorldText(TextPlacement.Wall, linesList, loc, rot);
                                if (!_currentTextByGroup.ContainsKey(rec.GroupNumber))
                                    _currentTextByGroup[rec.GroupNumber] = new List<int>();
                                _currentTextByGroup[rec.GroupNumber].Add(id);
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error spawning DB wall text in LoadWorldTextFromDb.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error loading wall text from database.");
                }
            });
        }

        private List<TextLine> GetTextLines(int groupNumber)
        {
            var linesList = new List<TextLine>();

            if (!Config.WorldText.TryGetValue(groupNumber, out var group) ||
                group == null || group.Lines == null || group.Lines.Count == 0)
            {
                Logger.LogWarning($"WorldText {groupNumber} not found in config.");
                return linesList;
            }

            string align = (group.TextAlignment ?? "center").Trim().ToLowerInvariant();
            const int edgePadChars = 2;
            string edgePad = new('\u00A0', edgePadChars);

            foreach (var raw in group.Lines)
            {
                if (string.IsNullOrWhiteSpace(raw))
                    continue;

                var (color, parsedText) = ParseColorAndText(raw);

                if (group.BgEnable)
                {
                    if (align == "left")
                        parsedText = edgePad + parsedText;
                    else if (align == "right")
                        parsedText += edgePad;
                }

                linesList.Add(new TextLine
                {
                    Text              = parsedText,
                    Color             = color,
                    FontSize          = group.FontSize,
                    FullBright        = true,
                    Scale             = group.TextScale,
                    JustifyHorizontal = GetTextAlignment(group.TextAlignment)
                });
            }

            if (group.BgEnable && linesList.Count > 0)
            {
                var first = linesList[0];

                first.BackgroundEnabled       = true;
                first.BackgroundAsSingleBlock = true;
                first.BackgroundHideText      = true;
                first.BackgroundFullBright    = true;
                first.BackgroundColor         = Color.FromArgb(255, 0, 0, 0);
                first.BackgroundWorldToUV     = 0.05f;
                first.BackgroundDepthOffset   = -0.50f;
                first.BackgroundBorderHeight  = 0.00f;
                first.BackgroundBorderWidth   = group.BgWidth;
                first.BackgroundMaxCharsPerLine = 32;
                first.BackgroundWidthInflation  = 1.0f;
                first.BackgroundPadChars        = 2;
            }

            return linesList;
        }

        private void DisplayConfiguredText()
        {
            Task.Run(() =>
            {
                foreach (var groupNumber in Config.WorldText.Keys)
                {
                    var linesList = GetTextLines(groupNumber);

                    Server.NextWorldUpdate(() =>
                    {
                        var checkAPI = Capability_SharedAPI.Get();
                        if (checkAPI != null)
                        {
                            if (_currentTextByGroup.TryGetValue(groupNumber, out var messageIDs))
                            {
                                foreach (int messageID in messageIDs)
                                {
                                    checkAPI.UpdateWorldText(messageID, linesList);
                                }
                            }
                        }
                    });
                }
            });
        }

        // Remove all current lists and reload them
        private void RefreshText()
        {
            var api = Capability_SharedAPI.Get();
            if (api != null)
            {
                foreach (var kvp in _currentTextByGroup)
                {
                    foreach (var id in kvp.Value) api.RemoveWorldText(id);
                }
            }
            _currentTextByGroup.Clear();

            if (Config.EnableDatabase)
                LoadWorldTextFromDb();
            else
                LoadWorldTextFromJson();
        }

        private static bool TryParseFloatInv(string s, out float f) =>
            float.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands,
                        CultureInfo.InvariantCulture, out f);

        private float DistanceTo(Vector a, Vector b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private Vector ParseVector(string s)
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !TryParseFloatInv(parts[0], out var x) ||
                !TryParseFloatInv(parts[1], out var y) ||
                !TryParseFloatInv(parts[2], out var z))
                throw new ArgumentException("Invalid vector string format.");
            return new Vector(x, y, z);
        }

        private QAngle ParseQAngle(string s)
        {
            var parts = s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 ||
                !TryParseFloatInv(parts[0], out var p) ||
                !TryParseFloatInv(parts[1], out var y) ||
                !TryParseFloatInv(parts[2], out var r))
                throw new ArgumentException("Invalid angle string format.");
            return new QAngle(p, y, r);
        }

        private PointWorldTextJustifyHorizontal_t GetTextAlignment(string? align)
        {
            switch ((align ?? "center").Trim().ToLowerInvariant())
            {
                case "left":
                    return PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_LEFT;
                case "right":
                    return PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_RIGHT;
                case "center":
                case "centre":
                case "middle":
                    return PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
                default:
                    Logger.LogWarning("Unknown textAlignment '{0}' - defaulting to center.", align);
                    return PointWorldTextJustifyHorizontal_t.POINT_WORLD_TEXT_JUSTIFY_HORIZONTAL_CENTER;
            }
        }

        private (Color color, string text) ParseColorAndText(string text)
        {
            var color = Color.White;
            var colorCodeMatch = System.Text.RegularExpressions.Regex.Match(text, @"^\{(\w+)\}");

            if (colorCodeMatch.Success)
            {
                var colorName = colorCodeMatch.Groups[1].Value;
                try
                {
                    color = Color.FromName(colorName);
                    if (!color.IsKnownColor)
                    {
                        color = Color.White;
                    }
                }
                catch
                {
                    color = Color.White;
                }
                text = text.Substring(colorCodeMatch.Length).Trim();
            }

            return (color, text);
        }
        
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };
    }

    public class WorldTextData
    {
        public int GroupNumber { get; set; }
        public required string Location { get; set; }
        public required string Rotation { get; set; }
    }
}