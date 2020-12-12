using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using System;
using NHSE.Core;
using SysBot.Base;

namespace SysBot.AnimalCrossing
{
    public sealed class CrossBot : SwitchRoutineExecutor<CrossBotConfig>
    {
        public readonly ConcurrentQueue<ItemRequest> Injections = new ConcurrentQueue<ItemRequest>();
        public bool CleanRequested { private get; set; }
        public string DodoCode { get; set; } = "No code set yet.";
        public ulong CoordinateAddress { get; set; } = 0x0;
        public byte[] InitialPlayerX { get; set; } = new byte[2];
        public byte[] InitialPlayerY { get; set; } = new byte[2];

        public CrossBot(CrossBotConfig cfg) : base(cfg) => State = new DropBotState(cfg.DropConfig);
        public readonly DropBotState State;

        public override void SoftStop() => Config.AcceptingCommands = false;

        protected override async Task MainLoop(CancellationToken token)
        {
            // Disconnect our virtual controller; will reconnect once we send a button command after a request.
            LogUtil.LogInfo("Detaching controller on startup as first interaction.", Config.IP);
            await Connection.SendAsync(SwitchCommand.DetachController(), token).ConfigureAwait(false);
            await Task.Delay(200, token).ConfigureAwait(false);

            // Validate inventory offset.
            LogUtil.LogInfo("Checking inventory offset for validity.", Config.IP);
            var valid = await GetIsPlayerInventoryValid(Config.Offset, token).ConfigureAwait(false);
            if (!valid)
            {
                LogUtil.LogInfo($"Inventory read from {Config.Offset} (0x{Config.Offset:X8}) does not appear to be valid.", Config.IP);
                if (Config.RequireValidInventoryMetadata)
                {
                    LogUtil.LogInfo("Exiting!", Config.IP);
                    return;
                }
            }
            
            // Check if AllowTeleporation is enabled in config.
            if (Config.AllowTeleporation)
            {
                // Obtain player coordinate address and store player's starting position
                LogUtil.LogInfo("Saving starting position.", Config.IP);
                CoordinateAddress = await GetCoordinateAddress(Config.CoordinatePointer, token).ConfigureAwait(false);
                InitialPlayerX = await Connection.ReadBytesAbsoluteAsync(CoordinateAddress, 0x2, token).ConfigureAwait(false);
                InitialPlayerY = await Connection.ReadBytesAbsoluteAsync(CoordinateAddress + 0x8, 0x2, token).ConfigureAwait(false);
            }

            // Check if DodoCodeRetrieval and AllowTeleporation are enabled in config.
            if (Config.DodoCodeRetrieval)
            {
                if (!Config.AllowTeleporation)
                {
                    LogUtil.LogInfo("AllowTeleporation has to be enabled to automatically retrieve Dodo code.", Config.IP);
                    return;
                }

                // Open gates and retrieve Dodo code in airport.
                LogUtil.LogInfo("Opening gates and obtaining Dodo code.", Config.IP);
                await GetDodoCode(token).ConfigureAwait(false);

                // Reset player position to initial position.
                LogUtil.LogInfo("Returning to starting position.", Config.IP);
                await ResetPosition(token).ConfigureAwait(false);
            }

            LogUtil.LogInfo("Successfully connected to bot. Starting main loop!", Config.IP);
            while (!token.IsCancellationRequested)
                await DropLoop(token).ConfigureAwait(false);
        }

        private async Task DropLoop(CancellationToken token)
        {
            if (!Config.AcceptingCommands)
            {
                await Task.Delay(1_000, token).ConfigureAwait(false);
                return;
            }

            if (Injections.TryDequeue(out var item))
            {
                var count = await DropItems(item, token).ConfigureAwait(false);
                State.AfterDrop(count);
            }
            else if ((State.CleanRequired && State.Config.AutoClean) || CleanRequested)
            {
                await CleanUp(State.Config.PickupCount, token).ConfigureAwait(false);
                State.AfterClean();
                CleanRequested = false;
            }
            else
            {
                State.StillIdle();
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task<bool> GetIsPlayerInventoryValid(uint playerOfs, CancellationToken token)
        {
            var (ofs, len) = InventoryValidator.GetOffsetLength(playerOfs);
            var inventory = await Connection.ReadBytesAsync(ofs, len, token).ConfigureAwait(false);

            return InventoryValidator.ValidateItemBinary(inventory);
        }

        private async Task<int> DropItems(ItemRequest drop, CancellationToken token)
        {
            int dropped = 0;
            bool first = true;
            foreach (var item in drop.Items)
            {
                await DropItem(item, first, token).ConfigureAwait(false);
                first = false;
                dropped++;
            }
            return dropped;
        }

        private async Task DropItem(Item item, bool first, CancellationToken token)
        {
            // Exit out of any menus.
            if (first)
            {
                for (int i = 0; i < 3; i++)
                    await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);
            }

            // Check if online session for game has ended.
            if (!await CheckSessionActive(token).ConfigureAwait(false))
            {
                LogUtil.LogInfo("Online session for your island was interrupted.", Config.IP);

                // Check if DodoCodeRetrieval is enabled in config.
                if (!Config.DodoCodeRetrieval)
                {
                    LogUtil.LogInfo("DodoCodeRetrieval and AllowTeleporation must be enabled to automatically retrieve new Dodo code. Exiting!", Config.IP);
                    return;
                }
                else if (!Config.AllowTeleporation)
                {
                    LogUtil.LogInfo("AllowTeleporation has to be enabled to automatically retrieve new Dodo code. Exiting!", Config.IP);
                    return;
                }

                // Open gates and retrieve Dodo code in airport.
                LogUtil.LogInfo("Opening gates and obtaining Dodo code.", Config.IP);
                await GetDodoCode(token).ConfigureAwait(false);

                // Reset player position to initial position.
                LogUtil.LogInfo("Returning to starting position.", Config.IP);
                await ResetPosition(token).ConfigureAwait(false);
            }

            var itemName = GameInfo.Strings.GetItemName(item);
            LogUtil.LogInfo($"Injecting Item: {item.DisplayItemId:X4} ({itemName}).", Config.IP);

            // Inject item.
            var data = item.ToBytesClass();
            var poke = SwitchCommand.Poke(Config.Offset, data);
            await Connection.SendAsync(poke, token).ConfigureAwait(false);
            await Task.Delay(0_300, token).ConfigureAwait(false);

            // Open player inventory and open the currently selected item slot -- assumed to be the config offset.
            await Click(SwitchButton.X, 1_100, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 0_500, token).ConfigureAwait(false);

            // Navigate down to the "drop item" option.
            var downCount = item.GetItemDropOption();
            for (int i = 0; i < downCount; i++)
                await Click(SwitchButton.DDOWN, 0_400, token).ConfigureAwait(false);

            // Reset player position to initial position if AllowTeleporation is enabled.
            if (Config.AllowTeleporation)
                await ResetPosition(token).ConfigureAwait(false);

            // Drop item, close menu.
            await Click(SwitchButton.A, 0_400, token).ConfigureAwait(false);
            await Click(SwitchButton.X, 0_400, token).ConfigureAwait(false);

            // Exit out of any menus (fail-safe)
            for (int i = 0; i < 2; i++)
                await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);
        }

        private async Task CleanUp(int count, CancellationToken token)
        {
            LogUtil.LogInfo("Picking up leftover items during idle time.", Config.IP);

            // Exit out of any menus.
            for (int i = 0; i < 3; i++)
                await Click(SwitchButton.B, 0_400, token).ConfigureAwait(false);

            // Pick up and delete.
            for (int i = 0; i < count; i++)
            {
                await Click(SwitchButton.Y, 2_000, token).ConfigureAwait(false);
                var poke = SwitchCommand.Poke(Config.Offset, Item.NONE.ToBytes());
                await Connection.SendAsync(poke, token).ConfigureAwait(false);
                await Task.Delay(1_000, token).ConfigureAwait(false);
            }
        }

        private async Task<ulong> GetCoordinateAddress(string pointer, CancellationToken token)
        {
            // Regex pattern to get operators and offsets from pointer expression.
            string pattern = @"(\+|\-)([A-Fa-f0-9]+)";
            Regex regex = new Regex(pattern);
            Match match = regex.Match(pointer);

            // Get first offset from pointer expression and read address at that offset from main start.
            var ofs = Convert.ToUInt64(match.Groups[2].Value, 16);
            var address = BitConverter.ToUInt64(await Connection.ReadBytesMainAsync(ofs, 0x8, token).ConfigureAwait(false), 0); 
            match = match.NextMatch();

            // Matches the rest of the operators and offsets in the pointer expression.
            while (match.Success)
            {
                // Get operator and offset from match.
                string opp = match.Groups[1].Value;
                ofs = Convert.ToUInt64(match.Groups[2].Value, 16);

                // Add or subtract the offset from the current stored address based on operator in front of offset.
                switch (opp)
                {
                    case "+":
                        address += ofs;
                        break;
                    case "-":
                        address -= ofs;
                        break;
                }

                // Attempt another match and if successful read bytes at address and store the new address.
                match = match.NextMatch();
                if (match.Success)
                {
                    byte[] bytes = await Connection.ReadBytesAbsoluteAsync(address, 0x8, token).ConfigureAwait(false);
                    address = BitConverter.ToUInt64(bytes, 0);
                }
            }
            
            return address;
        }

        private async Task GetDodoCode(CancellationToken token)
        {
            // Teleport player to airport entrance and set rotation to face doorway.
            await Connection.WriteBytesAbsoluteAsync(new byte[] { 64, 68, 0, 0, 0, 0, 0, 0, 132, 68 }, CoordinateAddress, token).ConfigureAwait(false);
            await Connection.WriteBytesAbsoluteAsync(new byte[] { 0, 0, 0, 112 }, CoordinateAddress + 0x3A, token).ConfigureAwait(false);

            // Walk through airport entrance.
            await SetStick(SwitchStick.LEFT, 20_000, 20_000, 1_000, token).ConfigureAwait(false);
            await SetStick(SwitchStick.LEFT, 0, 0, 9_000, token).ConfigureAwait(false);

            // Get player's coordinate address when inside airport and teleport player to Dodo.
            var AirportCoordinateAddress = await GetCoordinateAddress(Config.CoordinatePointer, token).ConfigureAwait(false);
            await Connection.WriteBytesAbsoluteAsync(new byte[] { 58, 67, 0, 0, 0, 0, 0, 0, 38, 67 }, AirportCoordinateAddress, token).ConfigureAwait(false);

            // Navigate through dialog with Dodo to open gates and to get Dodo code.
            var Hold = SwitchCommand.Hold(SwitchButton.L);
            await Connection.SendAsync(Hold, token).ConfigureAwait(false);
            await Task.Delay(0_500).ConfigureAwait(false);
            await Click(SwitchButton.A, 3_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_500, token).ConfigureAwait(false);
            await Click(SwitchButton.DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_000, token).ConfigureAwait(false);
            await Click(SwitchButton.DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 12_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_500, token).ConfigureAwait(false);
            await Click(SwitchButton.DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(SwitchButton.DDOWN, 0_300, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_000, token).ConfigureAwait(false);
            await Click(SwitchButton.DUP, 0_300, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 1_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_500, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 3_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_000, token).ConfigureAwait(false);
            await Click(SwitchButton.A, 2_000, token).ConfigureAwait(false);
            var Release = SwitchCommand.Release(SwitchButton.L);
            await Connection.SendAsync(Release, token).ConfigureAwait(false);

            // Obtain Dodo code from offset and store it.
            byte[] bytes = await Connection.ReadBytesAsync(0xA95E0F4, 0x5, token).ConfigureAwait(false);
            DodoCode = System.Text.Encoding.UTF8.GetString(bytes, 0, 5);
            LogUtil.LogInfo($"Retrieved Dodo code: {DodoCode}.", Config.IP);

            // Teleport into warp zone to leave airport
            await Connection.WriteBytesAbsoluteAsync(new byte[] { 32, 67, 0, 0, 0, 0, 0, 0, 120, 67 }, AirportCoordinateAddress, token).ConfigureAwait(false);

            // Wait for loading screen.
            while (!await IsOverworld(token))
                await Task.Delay(0_500).ConfigureAwait(false);
        }

        private async Task ResetPosition(CancellationToken token)
        {
            // Sets player xy coordinates to their initial values when bot was started and set player rotation to 0.
            await Connection.WriteBytesAbsoluteAsync(new byte[] { InitialPlayerX[0], InitialPlayerX[1], 0, 0, 0, 0, 0, 0, InitialPlayerY[0], InitialPlayerY[1] }, CoordinateAddress, token).ConfigureAwait(false);
            await Connection.WriteBytesAbsoluteAsync(new byte[] { 0, 0, 0, 0 }, CoordinateAddress + 0x3A, token).ConfigureAwait(false);
        }

        private async Task<bool> IsOverworld(CancellationToken token)
        {
            // Checks if player is in overworld (outside of a building).
            var x = BitConverter.ToUInt32(await Connection.ReadBytesAbsoluteAsync(CoordinateAddress + 0x1E, 0x4, token).ConfigureAwait(false), 0);
            return x == 0xC0066666;
        }

        private async Task<bool> CheckSessionActive(CancellationToken token)
        {
            // Checks if the session is still active and gates are still open. (Can close due to a player disconnecting while flying to your island.)
            var x = await Connection.ReadBytesAsync(0x91DD740, 0x1, token).ConfigureAwait(false);
            return x[0] == 1;
        }
    }
}
