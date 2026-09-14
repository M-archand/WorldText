using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CS2MenuManager.API.Class;
using CS2MenuManager.API.Menu;
using CS2MenuManager.API.Enum;
using CS2MenuManager.API.Interface;
using Dapper;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace WorldText
{
    public partial class PluginWorldText : BasePlugin, IPluginConfig<PluginConfig>
    {
        private static Vector ZeroVec() => new Vector(0, 0, 0);
        private static QAngle ZeroAng() => new QAngle(0, 0, 0);

        private sealed class DbPlacementRaw
        {
            public ulong Id { get; init; }
            public int GroupNumber { get; init; }
            public string Loc { get; init; } = ""; // "X Y Z"
            public string Ang { get; init; } = ""; // "P Y R"
        }

        private sealed class DbPlacement
        {
            public ulong Id { get; init; }
            public int GroupNumber { get; init; }
            public required Vector Pos { get; set; }
            public required QAngle Rot { get; set; }

            // E.g. "Group 2  (1822, 417, 1256)"
            public string Label =>
                $"Group {GroupNumber} ({Pos.X:0}, {Pos.Y:0}, {Pos.Z:0})";
        }

        // Called by !mtext
        private void OnTextMove(CCSPlayerController? player, CommandInfo _)
        {
            if (player is null || !player.IsValid)
                    return;
            
            if (!AdminManager.PlayerHasPermissions(player, Config.CommandPermission))
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}You do not have the correct permission to execute this command.");
                return;
            }

            if (!_hasMenuManager)
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}CS2MenuManager API not found! Move menu command is not available.");
                return;
            }

            if (!Config.EnableDatabase)
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed} This tool currently supports DB-saved lists only. Enable SaveToDb in config.");
                return;
            }

            if (string.IsNullOrEmpty(Server.MapName))
            {
                player.PrintToChat($"{chatPrefix} {ChatColors.LightRed}Current map is unknown.");
                return;
            }

            ShowPlacementsRootMenu(player);
        }

        private BaseMenu CreateMenu(string title)
        {
            try
            {
                // Options: WasdMenu/ChatMenu/CenterHtmlMenu/ConsoleMenu/PlayerMenu
                return MenuManager.MenuByType(Config.MenuType, title, this);
            }
            catch // Fallback
            {
                return MenuManager.MenuByType(typeof(WasdMenu), title, this);
            }
        }

        // Level 1: show all placements on this map
        private void ShowPlacementsRootMenu(CCSPlayerController player)
        {
            var map = Server.MapName;
            int playerSlot = player.Slot;

            _ = Task.Run(async () =>
            {
                List<DbPlacementRaw> raw;
                try
                {
                    raw = await GetAllPlacementsForMapRaw(map);
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[World-Text] Failed to load DB placements for menu.");
                    PrintToSlot(playerSlot, $"{chatPrefix} {ChatColors.LightRed}Failed to load placements from DB.");
                    return;
                }

                Server.NextFrame(() =>
                {
                    var owner = ValidPlayer(playerSlot);
                    if (owner is null) return;

                    var slots = new List<DbPlacement>(raw.Count);
                    foreach (var r in raw)
                    {
                        if (!PlacementFormat.TryParseVector(r.Loc, out var pos)) continue;
                        if (!PlacementFormat.TryParseQAngle(r.Ang, out var rot)) continue;

                        slots.Add(new DbPlacement
                        {
                            Id = r.Id,
                            GroupNumber = r.GroupNumber,
                            Pos = pos,
                            Rot = rot
                        });
                    }

                    if (slots.Count == 0)
                    {
                        owner.PrintToChat($"{chatPrefix} {ChatColors.LightRed}No Wall-Text placements found for this map.");
                        return;
                    }

                    var root = CreateMenu("Wall-Text Placements");
                    foreach (var slot in slots.OrderBy(s => s.GroupNumber).ThenBy(s => s.Id))
                    {
                        var captured = slot;
                        var opt = root.AddItem(captured.Label, (p, _) =>
                        {
                            ShowEditMenu(p, captured, root);
                        });
                        opt.PostSelectAction = PostSelectAction.Nothing;
                    }
                    root.Display(owner, 0);
                });
            });
        }

        // Level 2: choose move or rotate
        private void ShowEditMenu(CCSPlayerController player, DbPlacement slot, IMenu prev)
        {
            var edit = CreateMenu($"Edit Group {slot.GroupNumber} • Id {slot.Id}");
            edit.PrevMenu = prev;

            var loc = edit.AddItem("Change Location", (p, _) => ShowMoveLocationMenu(p, slot, edit));
            loc.PostSelectAction = PostSelectAction.Nothing;

            var ang = edit.AddItem("Change Angle", (p, _) => ShowMoveAngleMenu(p, slot, edit));
            ang.PostSelectAction = PostSelectAction.Nothing;

            edit.Display(player, 0);
        }

        // Level 3a: translate
        private void ShowMoveLocationMenu(CCSPlayerController player, DbPlacement slot, IMenu prev)
        {

            float step = MathF.Max(0.01f, (float)Config.MoveDistance);
            string s = step.ToString("0.##", CultureInfo.InvariantCulture);

            var m = CreateMenu($"Move Group {slot.GroupNumber} • Id {slot.Id}");
            m.PrevMenu = prev;

            AddMove(m, $"(Y - {s})",           slot, new Vector(0, -step, 0), ZeroAng());
            AddMove(m, $"(Y + {s})",           slot, new Vector(0, +step, 0), ZeroAng());
            AddMove(m, $"(X + {s})",           slot, new Vector(+step, 0, 0), ZeroAng());
            AddMove(m, $"(X - {s})",           slot, new Vector(-step, 0, 0), ZeroAng());
            AddMove(m, $"Move Up (Z + {s})",   slot, new Vector(0, 0, +step), ZeroAng());
            AddMove(m, $"Move Down (Z - {s})", slot, new Vector(0, 0, -step), ZeroAng());

            m.Display(player, 0);
        }

        // Level 3b: rotate
        private void ShowMoveAngleMenu(CCSPlayerController player, DbPlacement slot, IMenu prev)
        {
            float step = MathF.Max(0.01f, (float)Config.MoveDistance);
            string s = step.ToString("0.##", CultureInfo.InvariantCulture);

            var m = CreateMenu($"Rotate Group {slot.GroupNumber} • Id {slot.Id}");
            m.PrevMenu = prev;

            AddMove(m, $"Pitch +{s}", slot, ZeroVec(), new QAngle(+step, 0, 0));
            AddMove(m, $"Pitch -{s}", slot, ZeroVec(), new QAngle(-step, 0, 0));
            AddMove(m, $"Yaw +{s}",   slot, ZeroVec(), new QAngle(0, +step, 0));
            AddMove(m, $"Yaw -{s}",   slot, ZeroVec(), new QAngle(0, -step, 0));
            AddMove(m, $"Roll +{s}",  slot, ZeroVec(), new QAngle(0, 0, +step));
            AddMove(m, $"Roll -{s}",  slot, ZeroVec(), new QAngle(0, 0, -step));

            m.Display(player, 0);
        }

        private void AddMove(BaseMenu menu, string text, DbPlacement slot, Vector dPos, QAngle dAng)
        {
            var item = menu.AddItem(text, (p, opt) =>
            {
                int idx = menu.ItemOptions.IndexOf(opt);

                var newPos = new Vector(slot.Pos.X + dPos.X, slot.Pos.Y + dPos.Y, slot.Pos.Z + dPos.Z);
                var newRot = new QAngle(slot.Rot.X + dAng.X, slot.Rot.Y + dAng.Y, slot.Rot.Z + dAng.Z);

                var playerSlot = p.Slot;
                int generation = _loadGeneration;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await UpdatePlacementInDb(slot.Id, newPos, newRot);
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "[World-Text] UpdatePlacementInDb failed for Id {Id}.", slot.Id);
                        PrintToSlot(playerSlot, $"{chatPrefix} {ChatColors.Red}Failed to save the new position to the database (check logs).");
                        return;
                    }

                    // Move only after the UPDATE has completed
                    QueueTextUpdate(generation, () =>
                    {
                        if (!TryMoveTrackedText(slot.Id, newPos, newRot))
                            RefreshText();
                    });
                });

                slot.Pos = newPos;
                slot.Rot = newRot;

                // Keep the same selection highlighted
                menu.Title = text.StartsWith("Move", StringComparison.OrdinalIgnoreCase)
                    ? $"Move Group {slot.GroupNumber} • Id {slot.Id}"
                    : $"Rotate Group {slot.GroupNumber} • Id {slot.Id}";

                Server.NextFrame(() =>
                {
                    var owner = ValidPlayer(playerSlot);
                    if (owner is not null)
                        menu.DisplayAt(owner, idx, menu.MenuTime);
                });
            });

            item.PostSelectAction = PostSelectAction.Nothing;
        }

        // Query all Wall-Text placements on this map
        private async Task<List<DbPlacementRaw>> GetAllPlacementsForMapRaw(string map)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            var rows = await conn.QueryAsync<DbPlacementRaw>(
                $@"SELECT `Id`, `GroupNumber`, `Location` AS Loc, `Angle` AS Ang
                FROM `{table}` WHERE `MapName` = @m
                ORDER BY `GroupNumber`, `Id`;", new { m = map });

            return rows.ToList();
        }

        // Update a single placement by Id
        private async Task UpdatePlacementInDb(ulong id, Vector location, QAngle rotation)
        {
            string table = $"{Config.DatabaseSettings.TableName}";
            using var conn = CreateDbConnection();

            var loc = PlacementFormat.Format(location);
            var ang = PlacementFormat.Format(rotation);

            await conn.ExecuteAsync(
                $@"UPDATE `{table}` SET `Location`=@loc, `Angle`=@ang WHERE `Id`=@id;",
                new { id, loc, ang });
        }
    }
}
