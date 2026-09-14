using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using System.Text.Json;
using System.Data;
using Microsoft.Extensions.Logging;
using Dapper;
using MySqlConnector;

namespace WorldText
{
    public partial class PluginWorldText : BasePlugin, IPluginConfig<PluginConfig>
    {
        private string? _connectionString;
        private volatile bool _tablesEnsured;

        private void InitializeDatabaseConnectionString()
        {
            _tablesEnsured = false;

            if (!Config.EnableDatabase)
            {
                _connectionString = null;
                return;
            }

            var csb = new MySqlConnectionStringBuilder
            {
                Server   = Config.DatabaseSettings.Host,
                Port     = (uint)Config.DatabaseSettings.Port,
                Database = Config.DatabaseSettings.Database,
                UserID   = Config.DatabaseSettings.Username,
                Password = Config.DatabaseSettings.Password,
                SslMode  = Enum.TryParse<MySqlSslMode>(Config.DatabaseSettings.SslMode, true, out var mode)
                           ? mode
                           : MySqlSslMode.None
            };

            _connectionString = csb.ToString();
        }

        private IDbConnection CreateDbConnection()
        {
            if (string.IsNullOrWhiteSpace(_connectionString))
                throw new InvalidOperationException("MySQL connection string not initialized");

            return new MySqlConnection(_connectionString);
        }

        private async Task EnsureTablesAsync()
        {
            if (!Config.EnableDatabase || _tablesEnsured) return;

            string table = $"{Config.DatabaseSettings.TableName}";
            string sql = $@"
                CREATE TABLE IF NOT EXISTS `{table}` (
                `Id` BIGINT UNSIGNED NOT NULL AUTO_INCREMENT,
                `MapName`     VARCHAR(255) NOT NULL,
                `GroupNumber` INT          NOT NULL,
                `Location`    VARCHAR(128) NOT NULL, -- ""X Y Z"" (InvariantCulture)
                `Angle`       VARCHAR(128) NOT NULL, -- ""P Y R"" (InvariantCulture)
                PRIMARY KEY (`Id`),
                KEY `idx_map` (`MapName`),
                KEY `idx_map_group` (`MapName`, `GroupNumber`),
                UNIQUE KEY `uniq_exact` (`MapName`,`GroupNumber`,`Location`,`Angle`)
                );";

            using var conn = CreateDbConnection();
            await conn.ExecuteAsync(sql);

            _tablesEnsured = true;
        }

        private sealed class WtTextRecord
        {
            public ulong Id { get; set; }
            public int GroupNumber { get; set; }
            public string Location { get; set; } = "";
            public string Angle { get; set; } = "";
        }

        private async Task DeleteWorldTextFromDb(string mapName, int group, Vector location, QAngle rotation)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            var loc = PlacementFormat.Format(location);
            var ang = PlacementFormat.Format(rotation);

            string sql = $@"
                DELETE FROM `{table}`
                WHERE `MapName`=@m AND `GroupNumber`=@g AND `Location`=@loc AND `Angle`=@ang
                LIMIT 1;";

            await conn.ExecuteAsync(sql, new { m = mapName, g = group, loc, ang });
        }

        // Imports any existing JSON text files into the database
        private void OnImportText(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || command == null) return;

            if (!AdminManager.PlayerHasPermissions(player, Config.CommandPermission))
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}You do not have permission to execute this command");
                return;
            }

            if (!Config.EnableDatabase)
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}EnableDatabase must be true to import into the database");
                return;
            }

            player.PrintToChat($"{chatPrefix} {ChatColors.White}Scanning {ChatColors.Lime}/plugins/WorldText/maps {ChatColors.White}folder");

            int generation = _loadGeneration;

            _ = Task.Run(async () =>
            {
                var mapsDir = Path.Combine(ModuleDirectory, "maps");
                if (!Directory.Exists(mapsDir))
                {
                    Server.NextWorldUpdate(() => player.PrintToChat($"{chatPrefix} {ChatColors.Red}No maps folder found at {mapsDir}"));
                    return;
                }

                string[] files;
                try
                {
                    files = Directory.GetFiles(mapsDir, "*.json", SearchOption.TopDirectoryOnly);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[World-Text] Error reading maps directory");
                    Server.NextWorldUpdate(() => player.PrintToChat($"{chatPrefix} {ChatColors.Red}Failed to read {mapsDir} (check logs)"));
                    return;
                }

                var firstFew = string.Join(", ", files.Take(5).Select(Path.GetFileName));

                var importQueue = new List<ImportEntry>();
                int filesTouched = 0;

                foreach (var filePath in files)
                {
                    var fileName = Path.GetFileName(filePath);

                    var baseName = Path.GetFileNameWithoutExtension(filePath);
                    if (baseName.EndsWith("_text", StringComparison.OrdinalIgnoreCase))
                        baseName = baseName[..^5];
                    var mapName = baseName;

                    filesTouched++;

                    List<WorldTextData>? data = null;
                    try
                    {
                        var json = await File.ReadAllTextAsync(filePath).ConfigureAwait(false);
                        data = JsonSerializer.Deserialize<List<WorldTextData>>(json, JsonOpts);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, $"[World-Text] Failed reading {fileName}; skipping");
                        continue;
                    }

                    if (data == null || data.Count == 0)
                    {
                        Logger.LogInformation($"[World-Text] {fileName}: 0 entries");
                        continue;
                    }

                    int ok = 0;
                    foreach (var entry in data)
                    {
                        try
                        {
                            if (entry.GroupNumber <= 0) continue;

                            if (!PlacementFormat.TryParse(entry.Location, out var fx, out var fy, out var fz))
                                throw new ArgumentException("Bad location");

                            if (!PlacementFormat.TryParse(entry.Rotation, out var fp, out var fyaw, out var fr))
                                throw new ArgumentException("Bad angle");

                            importQueue.Add(new ImportEntry
                            {
                                MapName = mapName,
                                GroupNumber = entry.GroupNumber,
                                X = fx, Y = fy, Z = fz,
                                Pitch = fp, Yaw = fyaw, Roll = fr
                            });
                            ok++;
                        }
                        catch (Exception ex)
                        {
                            Logger.LogWarning($"[World-Text] {fileName}: skipping malformed entry (#{importQueue.Count + 1}). {ex.Message}");
                            continue;
                        }
                    }

                    Logger.LogInformation($"[World-Text] {fileName}: queued {ok} / {data.Count}.");
                }

                var totalQueued = importQueue.Count;

                Server.NextWorldUpdate(() =>
                {
                    player.PrintToChat($"{chatPrefix} {ChatColors.White}Queued {totalQueued} placements from {filesTouched} files");
                    if (totalQueued == 0)
                        player.PrintToChat($"{chatPrefix} {ChatColors.Red}Nothing to import");
                });

                if (totalQueued == 0) return;

                int inserted;
                try
                {
                    inserted = await SaveWorldTextBatchToDb(importQueue).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[World-Text] Import batch insert failed");
                    Server.NextWorldUpdate(() => player.PrintToChat($"{chatPrefix} {ChatColors.Red}Database import failed (check logs)"));
                    return;
                }

                QueueTextUpdate(generation, RefreshText);
                Server.NextWorldUpdate(() =>
                    player.PrintToChat($"{chatPrefix} {ChatColors.Lime}Database import completed! {ChatColors.White}{inserted}{ChatColors.Lime} of {ChatColors.White}{totalQueued}{ChatColors.Lime} placements imported!")
                );
            });
        }

        private const int ImportBatchSize = 200;

        private async Task<int> SaveWorldTextBatchToDb(IReadOnlyList<ImportEntry> entries)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            int inserted = 0;

            for (int offset = 0; offset < entries.Count; offset += ImportBatchSize)
            {
                int count = Math.Min(ImportBatchSize, entries.Count - offset);

                var rows = new List<string>(count);
                var parameters = new DynamicParameters();

                for (int i = 0; i < count; i++)
                {
                    var e = entries[offset + i];

                    rows.Add($"(@m{i},@g{i},@loc{i},@ang{i})");
                    parameters.Add($"m{i}", e.MapName);
                    parameters.Add($"g{i}", e.GroupNumber);
                    parameters.Add($"loc{i}", PlacementFormat.Format(e.X, e.Y, e.Z));
                    parameters.Add($"ang{i}", PlacementFormat.Format(e.Pitch, e.Yaw, e.Roll));
                }

                string sql = $@"
                    INSERT IGNORE INTO `{table}` (`MapName`,`GroupNumber`,`Location`,`Angle`)
                    VALUES {string.Join(",", rows)};";

                inserted += await conn.ExecuteAsync(sql, parameters).ConfigureAwait(false);
            }

            return inserted;
        }

        private sealed class ImportEntry
        {
            public required string MapName { get; init; }
            public int GroupNumber { get; init; }
            public float X { get; init; }
            public float Y { get; init; }
            public float Z { get; init; }
            public float Pitch { get; init; }
            public float Yaw   { get; init; }
            public float Roll  { get; init; }
        }
    }
}