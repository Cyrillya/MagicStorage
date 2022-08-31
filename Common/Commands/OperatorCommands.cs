﻿using MagicStorage.Common.Players;
using MagicStorage.Common.Systems;
using Microsoft.Xna.Framework;
using System.Collections.Generic;
using System.Linq;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;

namespace MagicStorage.Common.Commands {
	internal class RequestOperator : ModCommand {
		public override string Command => "reqop";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => "[c/ff6a00:Usage: /reqop]";

		public override string Description => "Requests operator status from the server";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply("Expected no arguments", Color.Red);
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer) {
				caller.Reply("This command can only be used by multiplayer clients", Color.Red);
				return;
			}

			if (caller.Player.GetModPlayer<OperatorPlayer>().hasOp) {
				caller.Reply("This player already has the Server Operator status", Color.Red);
				return;
			}

			NetHelper.ClientRequestServerOperator();
		}
	}

	internal class GiveOperator : ModCommand {
		public override string Command => "giveop";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => "[c/ff6a00:Usage: /giveop <number>]";

		public override string Description => "Gives operator status to client <number>";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 1) {
				caller.Reply("Expected one integer argument", Color.Red);
				return;
			}

			if (!int.TryParse(args[0], out int client) || client < 0 || client > 255) {
				caller.Reply("Argument was not a positive integer between 0 and 255", Color.Red);
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer) {
				caller.Reply("This command can only be used by multiplayer clients", Color.Red);
				return;
			}

			if (!caller.Player.GetModPlayer<OperatorPlayer>().hasOp) {
				caller.Reply("This command can only be used by multiplayer clients with the Server Operator status", Color.Red);
				return;
			}

			Player plr = Main.player[client];

			if (!plr.active) {
				caller.Reply("Client " + client + " is not connected to the server", Color.Red);
				return;
			}

			var mp = plr.GetModPlayer<OperatorPlayer>();

			if (mp.hasOp) {
				caller.Reply("Client " + client + " already has the Server Operator status", Color.Red);
				return;
			}

			mp.hasOp = true;

			NetHelper.ClientSendPlayerHasOp(client);

			Netcode.ClientPrintKeyReponse(valid: true);
		}
	}

	internal class WhoAreClients : ModCommand {
		public override string Command => "whois";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => "[c/ff6a00:Usage: /whois]";

		public override string Description => "Prints what each player's client index is";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply("Expected no arguments", Color.Red);
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer) {
				caller.Reply("This command can only be used by multiplayer clients", Color.Red);
				return;
			}

			if (!caller.Player.GetModPlayer<OperatorPlayer>().hasOp) {
				caller.Reply("This command can only be used by multiplayer clients with the Server Operator status", Color.Red);
				return;
			}

			IEnumerable<string> print = Main.player
				.Select((p, i) => (player: p, whoAmI: i))
				.Where(t => t.player.active)
				.Select(t => $"[{t.whoAmI}]: {t.player.name}")
				.Prepend("Players:");

			caller.Reply(string.Join("\n  ", print), Color.LightGray);
		}
	}

	internal class WhoAmI : ModCommand {
		public override string Command => "whoami";

		public override CommandType Type => CommandType.Chat;

		public override string Usage => "[c/ff6a00:Usage: /whoami]";

		public override string Description => "Prints this player's client index";

		public override void Action(CommandCaller caller, string input, string[] args) {
			if (args.Length != 0) {
				caller.Reply("Expected no arguments", Color.Red);
				return;
			}

			if (Main.netMode == NetmodeID.SinglePlayer) {
				caller.Reply("This command can only be used by multiplayer clients", Color.Red);
				return;
			}

			caller.Reply("You are client " + caller.Player.whoAmI, Color.LightGray);
		}
	}
}
