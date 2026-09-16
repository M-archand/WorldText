using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using CS2MenuManager.API.Class;
using K4WorldTextSharedAPI;
using System.Drawing;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Logging;


namespace WorldText
{
    [MinimumApiVersion(369)]
    public partial class PluginWorldText : BasePlugin, IPluginConfig<PluginConfig>
    {
        public override string ModuleName => "World Text";
        public override string ModuleAuthor => "Marchand";
        public override string ModuleVersion => "2.0.0";
        public required PluginConfig Config { get; set; } = new PluginConfig();
        public static PluginCapability<IK4WorldTextProvider> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");
        private bool _hasMenuManager;
        private readonly Dictionary<int, List<int>> _currentTextByGroup = new();
        private readonly Dictionary<ulong, int> _k4IdByDbId = new();
        private string? _textLoadedForMap;
        private int _loadGeneration;
        private bool _unloaded;
        private readonly SemaphoreSlim _jsonFileLock = new(1, 1);
        private static readonly string chatPrefix = $" {ChatColors.Purple}[{ChatColors.LightPurple}World-Text{ChatColors.Purple}]";
        private readonly JsonSerializerOptions jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            if (TryGetSharedApi() is null)
                Logger.LogError("You don't have K4-WorldText-API installed. It is required. Download it from https://github.com/M-archand/K4-WorldText-API/releases");


            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                int generation = _loadGeneration;
                QueueTextUpdate(generation, () => EnsureTextLoaded(mapName));
            });

            AddTimer(3, () => EnsureTextLoaded(Server.MapName), TimerFlags.STOP_ON_MAPCHANGE);

            RegisterListener<Listeners.OnMapEnd>(() =>
            {
                ClearTrackedText();
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
        }

        public void OnConfigParsed(PluginConfig config)
        {
            Config = config;

            const int ExpectedVersion = 3;
            if (Config.Version < ExpectedVersion)
                Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", ExpectedVersion, Config.Version);

            if (Config.EnableDatabase)
                InitializeDatabaseConnectionString();


            AddCommand($"css_{Config.AddCommand}", "Add text in front of you", OnTextAdd);
            AddCommand($"css_{Config.RemoveCommand}", "Removes the closest group of text", OnTextRemove);
            AddCommand($"css_{Config.MoveCommand}", "Opens a menu to adjust the text location/angles", OnTextMove);
            AddCommand("css_importtext", "Imports any existing JSON text locations into the database", OnImportText);
            AddCommand("css_reloadtext", "Refresh the config and reload the text in the world.", OnRefreshText);
        }

        public override void Unload(bool hotReload)
        {
            _unloaded = true;

            try
            {
                TryGetSharedApi()?.RemoveAll();
            }
            finally
            {
                ClearTrackedText();
            }
        }

        private string MapJsonPath(string mapName) =>
            Path.Combine(ModuleDirectory, "maps", $"{mapName}.json");

        private List<WorldTextData>? ReadMapJson(string path) =>
            File.Exists(path)
                ? JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path))
                : null;

        private void WriteMapJson(string path, List<WorldTextData> data) =>
            File.WriteAllText(path, JsonSerializer.Serialize(data, jsonOptions));

        private void SaveWorldTextToFile(Vector location, QAngle rotation, int groupNumber)
        {
            var path = MapJsonPath(Server.MapName);
            var worldTextData = new WorldTextData
            {
                GroupNumber = groupNumber,
                Location = PlacementFormat.Format(location),
                Rotation = PlacementFormat.Format(rotation)
            };

            _ = Task.Run(async () =>
            {
                await _jsonFileLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var data = ReadMapJson(path) ?? new List<WorldTextData>();
                    data.Add(worldTextData);
                    WriteMapJson(path, data);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to save the placement to {Path}.", path);
                }
                finally
                {
                    _jsonFileLock.Release();
                }
            });
        }

        private async Task SaveWorldTextToDb(string mapName, int group, Vector location, QAngle rotation)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            var loc = PlacementFormat.Format(location);
            var ang = PlacementFormat.Format(rotation);

            string sql = $@"
                    INSERT IGNORE INTO `{table}` (`MapName`,`GroupNumber`,`Location`,`Angle`)
                    VALUES (@m,@g,@loc,@ang);";

            await conn.ExecuteAsync(sql, new { m = mapName, g = group, loc, ang });
        }

        private async Task<bool> RemoveClosestDbText(string mapName, Vector playerPos, int playerSlot)
        {
            int generation = _loadGeneration;

            try
            {
                var checkAPI = TryGetSharedApi();
                if (checkAPI is null)
                {
                    PrintToSlot(playerSlot, $"{chatPrefix} {ChatColors.LightRed}K4-WorldText-API missing.");
                    return false;
                }

                PruneDeadTextIds(checkAPI);

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
                    PrintToSlot(playerSlot, $"{chatPrefix} {ChatColors.LightRed}Move closer to the text you want to remove.");
                    return false;
                }

                // Remove from the world
                RemoveTrackedText(checkAPI, targetMsgId.Value);
                ForgetDbLink(targetMsgId.Value);
                if (_currentTextByGroup.TryGetValue(targetGroup, out var list))
                    list.Remove(targetMsgId.Value);

                // Delete the matching row from DB
                await DeleteWorldTextFromDb(mapName, targetGroup, targetLoc, targetAng);

                PrintToSlot(playerSlot, $"{chatPrefix} {ChatColors.Lime}Removed one placement from {ChatColors.White}Group {targetGroup} {ChatColors.Lime}on {ChatColors.White}{mapName}");
                QueueTextUpdate(generation, RefreshText);

                return true;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "RemoveClosestDbText failed");
                return false;
            }
        }

        private void RemoveClosestJsonText(CCSPlayerController player)
        {
            var checkAPI = TryGetSharedApi();
            if (checkAPI is null) return;

            PruneDeadTextIds(checkAPI);

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
                player.PrintToChat($"{chatPrefix} {ChatColors.Red}Move within 200 units of the text that you want to remove.");
                return;
            }

            RemoveTrackedText(checkAPI, target.Id);

            if (groupWithTarget != -1)
            {
                _currentTextByGroup[groupWithTarget].Remove(target.Id);
            }

            var mapName = Server.MapName;
            var path = MapJsonPath(mapName);

            Vector entityVector = target.Entity.AbsOrigin;
            QAngle entityAngle = target.Entity.AbsRotation;
            float targetX = entityVector.X, targetY = entityVector.Y;
            float targetPitch = entityAngle.X, targetYaw = entityAngle.Y, targetRoll = entityAngle.Z;

            _ = Task.Run(async () =>
            {
                await _jsonFileLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    var data = ReadMapJson(path);
                    if (data == null) return;

                    data.RemoveAll(x =>
                    {
                        if (!PlacementFormat.TryParse(x.Location, out var lx, out var ly, out _)) return false;
                        if (!PlacementFormat.TryParse(x.Rotation, out var pitch, out var yaw, out var roll)) return false;

                        return lx == targetX &&
                            ly == targetY &&
                            pitch == targetPitch &&
                            yaw == targetYaw &&
                            roll == targetRoll;
                    });

                    WriteMapJson(path, data);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to remove the placement from {Path}.", path);
                }
                finally
                {
                    _jsonFileLock.Release();
                }
            });

            player.PrintToChat($"{chatPrefix} {ChatColors.Lime}Removed one placement from {ChatColors.White}Group {groupWithTarget} {ChatColors.Lime}on {ChatColors.White}{mapName}");
        }

        private void LoadWorldTextFromJson(int generation, string? passedMapName = null)
        {
            if (!IsCurrentGeneration(generation)) return;

            var path = MapJsonPath(passedMapName ?? Server.MapName);

            _ = Task.Run(async () =>
            {
                List<WorldTextData>? data = null;

                await _jsonFileLock.WaitAsync().ConfigureAwait(false);
                try
                {
                    data = ReadMapJson(path);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Failed to read the placements in {Path}.", path);
                }
                finally
                {
                    _jsonFileLock.Release();
                }

                if (data == null || data.Count == 0) return;

                QueueTextUpdate(generation, () =>
                {
                    try
                    {
                        var checkAPI = TryGetSharedApi();
                        if (checkAPI is null) return;

                        foreach (var worldTextData in data)
                        {
                            if (!PlacementFormat.TryParseVector(worldTextData.Location, out var location) ||
                                !PlacementFormat.TryParseQAngle(worldTextData.Rotation, out var rotation))
                            {
                                Logger.LogWarning("Skipping malformed placement in {Path}: '{Location}' / '{Rotation}'.", path, worldTextData.Location, worldTextData.Rotation);
                                continue;
                            }

                            var linesList = GetTextLines(worldTextData.GroupNumber);
                            if (linesList.Count == 0)
                                continue;

                            var messageID = checkAPI.AddWorldText(TextPlacement.Wall, linesList, location, rotation);
                            if (!_currentTextByGroup.ContainsKey(worldTextData.GroupNumber))
                                _currentTextByGroup[worldTextData.GroupNumber] = new List<int>();
                            _currentTextByGroup[worldTextData.GroupNumber].Add(messageID);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error spawning JSON wall text in LoadWorldTextFromJson.");
                    }
                });
            });
        }

        private void LoadWorldTextFromDb(int generation)
        {
            if (!IsCurrentGeneration(generation)) return;

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

                    QueueTextUpdate(generation, () =>
                    {
                        try
                        {
                            var api = TryGetSharedApi();
                            if (api is null) return;

                            foreach (var rec in rows)
                            {
                                var linesList = GetTextLines(rec.GroupNumber);
                                if (linesList.Count == 0)
                                    continue;

                                var loc = PlacementFormat.ParseVector(rec.Location);
                                var rot = PlacementFormat.ParseQAngle(rec.Angle);

                                var id = api.AddWorldText(TextPlacement.Wall, linesList, loc, rot);
                                if (!_currentTextByGroup.ContainsKey(rec.GroupNumber))
                                    _currentTextByGroup[rec.GroupNumber] = new List<int>();
                                _currentTextByGroup[rec.GroupNumber].Add(id);
                                _k4IdByDbId[rec.Id] = id;
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

        private IK4WorldTextSharedAPI? TryGetSharedApi()
        {
            try
            {
                return Capability_SharedAPI.Get()?.ForPlugin(this);
            }
            catch (KeyNotFoundException)
            {
                return null;
            }
        }

        private void ClearTrackedText()
        {
            _currentTextByGroup.Clear();
            _k4IdByDbId.Clear();
            _textLoadedForMap = null;
            _loadGeneration++;
        }

        private bool IsCurrentGeneration(int generation) => !_unloaded && generation == _loadGeneration;

        private static CCSPlayerController? ValidPlayer(int slot)
        {
            var player = Utilities.GetPlayerFromSlot(slot);
            return player != null && player.IsValid ? player : null;
        }

        private static void PrintToSlot(int slot, string message)
        {
            Server.NextWorldUpdate(() => ValidPlayer(slot)?.PrintToChat(message));
        }

        private void QueueTextUpdate(int generation, Action action)
        {
            Server.NextWorldUpdate(() =>
            {
                if (!IsCurrentGeneration(generation)) return;
                action();
            });
        }

        // K4 with throw for ids it no longer tracks,
        // therefore, treat those as already gone.
        private void RemoveTrackedText(IK4WorldTextSharedAPI api, int id)
        {
            try
            {
                api.RemoveWorldText(id);
            }
            catch (KeyNotFoundException ex)
            {
                Logger.LogDebug(ex, "WorldText id {Id} was already removed from K4-WorldText-API.", id);
            }
        }

        private void ForgetDbLink(int k4Id)
        {
            foreach (var dbId in _k4IdByDbId.Where(kvp => kvp.Value == k4Id).Select(kvp => kvp.Key).ToList())
                _k4IdByDbId.Remove(dbId);
        }

        // Move one placement without touching the rest of the map
        private bool TryMoveTrackedText(ulong dbId, Vector location, QAngle rotation)
        {
            if (!_k4IdByDbId.TryGetValue(dbId, out var k4Id))
                return false;

            var api = TryGetSharedApi();
            if (api is null)
                return false;

            try
            {
                api.TeleportWorldText(k4Id, location, rotation);
                return true;
            }
            catch (KeyNotFoundException ex)
            {
                Logger.LogWarning(ex, "TeleportWorldText failed for id {Id}; falling back to a full refresh.", k4Id);
                _k4IdByDbId.Remove(dbId);
                return false;
            }
        }

        // Drop tracked ids that K4 no longer knows
        private void PruneDeadTextIds(IK4WorldTextSharedAPI api)
        {
            foreach (var ids in _currentTextByGroup.Values)
                ids.RemoveAll(id => api.GetWorldTextLineEntities(id) is null);
        }

        private void EnsureTextLoaded(string mapName)
        {
            if (_unloaded || string.IsNullOrEmpty(mapName) || _textLoadedForMap == mapName)
                return;

            if (TryGetSharedApi() is null)
            {
                Logger.LogWarning("K4-WorldText-API not available yet, skipping text load for {Map}.", mapName);
                return;
            }

            _textLoadedForMap = mapName;
            int generation = _loadGeneration;

            if (!Config.EnableDatabase)
            {
                LoadWorldTextFromJson(generation, mapName);
                return;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await EnsureTablesAsync().ConfigureAwait(false);
                    QueueTextUpdate(generation, () => LoadWorldTextFromDb(generation));
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error loading WorldText info from database. Please check your credentials.");
                    QueueTextUpdate(generation, () => LoadWorldTextFromJson(generation));
                }
            });
        }

        // Remove all current text and reload it
        private void RefreshText()
        {
            if (_unloaded) return;

            try
            {
                TryGetSharedApi()?.RemoveAll();
            }
            finally
            {
                ClearTrackedText();
            }

            EnsureTextLoaded(Server.MapName);
        }

        private float DistanceTo(Vector a, Vector b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
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