using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Extensions;
using Microsoft.Extensions.Logging;
using K4WorldTextSharedAPI;

namespace WorldText
{
    public partial class PluginWorldText : BasePlugin, IPluginConfig<PluginConfig>
    {
        // The handler for the !refreshtext command
        private void OnRefreshText(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || command == null) return;

            if (!AdminManager.PlayerHasPermissions(player, Config.CommandPermission))
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}You do not have permission to use this command.");
                return;
            }

            Config.Reload();
            Server.NextWorldUpdate(() => RefreshText());
            player.PrintToChat($" {ChatColors.Lime}WorldText config & placement has been reloaded!");
        }

        // The handler for when !text <#> is called
        private void OnTextAdd(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || command == null) return;

            if (!AdminManager.PlayerHasPermissions(player, Config.CommandPermission))
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}You do not have permission to use this command.");
                return;
            }

            var map = Server.MapName;

            var api = Capability_SharedAPI.Get();
            if (api is null)
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}K4-WorldText-API missing.");
                return;
            }

            if (command.ArgCount < 2 || !int.TryParse(command.GetArg(1), out var groupNumber))
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.White}Usage: {ChatColors.LightRed}!{Config.AddCommand} <groupNumber>");
                return;
            }

            if (!Config.WorldText.TryGetValue(groupNumber, out var group) || group == null || group.Lines == null || group.Lines.Count == 0)
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}Group {groupNumber} was not found in the config. Please create it first.");
                return;
            }

            var linesList = GetTextLines(groupNumber);

            Server.NextWorldUpdate(() =>
            {
                try
                {
                    int messageID = api.AddWorldTextAtPlayer(player, TextPlacement.Wall, linesList);

                    if (!_currentTextByGroup.ContainsKey(groupNumber))
                        _currentTextByGroup[groupNumber] = new List<int>();
                    _currentTextByGroup[groupNumber].Add(messageID);

                    var lineList = api.GetWorldTextLineEntities(messageID);
                    if (lineList?.Count > 0)
                    {
                        var location = lineList[0]?.AbsOrigin;
                        var rotation = lineList[0]?.AbsRotation;

                        if (location != null && rotation != null)
                        {
                            // Apply per-group Z offset (units off ground)
                            if (Config.WorldText.TryGetValue(groupNumber, out var group) && Math.Abs(group.ZOffset) > 0.001f)
                            {
                                var lifted = new Vector(location.X, location.Y, location.Z + group.ZOffset);

                                try
                                {
                                    api.TeleportWorldText(messageID, lifted, rotation, modifyConfig: false);
                                    location = lifted;
                                }
                                catch (Exception te)
                                {
                                    Logger.LogWarning(te, $"TeleportWorldText failed for group {groupNumber}; saving original location without ZOffset.");
                                }
                            }

                            if (Config.EnableDatabase)
                                _ = SaveWorldTextToDb(map, groupNumber, location, rotation);
                            else
                                SaveWorldTextToFile(location, rotation, groupNumber);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error during NextWorldUpdate for OnTextAdd.");
                }
            });
        }

        // The handler for when !removetext is called
        public void OnTextRemove(CCSPlayerController? player, CommandInfo? command)
        {
            if (player == null || command == null) return;

            if (!AdminManager.PlayerHasPermissions(player, Config.CommandPermission))
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}You do not have permission to use this command.");
                return;
            }

            var mapName = Server.MapName;
            var api = Capability_SharedAPI.Get();
            if (api == null)
            {
                command.ReplyToCommand($"{chatPrefix} {ChatColors.LightRed}K4-WorldText-API missing.");
                return;
            }

            var pawn = player.PlayerPawn?.Value;
            var atPos = pawn?.AbsOrigin ?? player.AbsOrigin!;

            if (Config.EnableDatabase)
            {
                _ = RemoveClosestDbText(mapName, atPos, player);
                return;
            }
            else
                Server.NextWorldUpdate(() => RemoveClosestJsonText(player, command));
        }
    }
}