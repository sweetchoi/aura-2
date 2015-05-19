﻿// Copyright (c) Aura development team - Licensed under GNU GPL
// For more information, see license file in the main folder

using Aura.Channel.Scripting.Scripts;
using Aura.Data;
using Aura.Mabi;
using Aura.Shared.Util;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Aura.Channel.Scripting
{
	/// <summary>
	/// Channel's script manager
	/// </summary>
	public class ScriptManager : Aura.Shared.Scripting.ScriptManager
	{
		/// <summary>
		/// Relative path to the system script folder
		/// </summary>
		private const string SystemIndexRoot = "system/scripts/";

		/// <summary>
		/// Relative path to the user script folder
		/// </summary>
		private const string UserIndexRoot = "user/scripts/";

		/// <summary>
		/// Relative path to the cache folder
		/// </summary>
		private const string CacheRoot = "cache/";

		/// <summary>
		/// Relative path to the primary script list
		/// </summary>
		private const string IndexPath = SystemIndexRoot + "scripts.txt";

		/// <summary>
		/// Item scripts loaded by the channel
		/// </summary>
		public ItemScriptCollection ItemScripts { get; private set; }

		/// <summary>
		/// AI scripts loaded by the channel
		/// </summary>
		public AiScriptCollection AiScripts { get; private set; }

		/// <summary>
		/// NPC shop scripts loaded by the channel
		/// </summary>
		public NpcShopScriptCollection NpcShopScripts { get; private set; }

		/// <summary>
		/// Quest scripts loaded by the channel
		/// </summary>
		public QuestScriptCollection QuestScripts { get; private set; }

		/// <summary>
		/// Hooks set up by various scripts
		/// </summary>
		public NpcScriptHookCollection NpcScriptHooks { get; private set; }

		/// <summary>
		/// Global scripting variables
		/// </summary>
		public ScriptVariables GlobalVars { get; protected set; }

		/// <summary>
		/// Creates new script manager
		/// </summary>
		public ScriptManager()
		{
			this.ItemScripts = new ItemScriptCollection();
			this.AiScripts = new AiScriptCollection();
			this.NpcShopScripts = new NpcShopScriptCollection();
			this.QuestScripts = new QuestScriptCollection();
			this.NpcScriptHooks = new NpcScriptHookCollection();

			this.GlobalVars = new ScriptVariables();
		}

		/// <summary>
		/// Sets up global variables and subscriptions, call once everything
		/// is ready (scripts, world, etc).
		/// </summary>
		public void Init()
		{
			this.GlobalVars.Perm = ChannelServer.Instance.Database.LoadVars("Aura System", 0);
			ChannelServer.Instance.Events.MabiTick += OnMabiTick;
		}

		/// <summary>
		/// Loads all scripts.
		/// </summary>
		public void Load()
		{
			this.CreateInlineItemScriptFile();
			this.LoadScripts(IndexPath);
			ChannelServer.Instance.World.SpawnManager.SpawnAll();
		}

		/// <summary>
		/// Removes all NPCs, props, etc and loads them again.
		/// </summary>
		public void Reload()
		{
			this.ItemScripts.Clear();
			this.AiScripts.Clear();
			this.NpcShopScripts.Clear();
			this.QuestScripts.Clear();
			this.NpcScriptHooks.Clear();

			this.DisposeScripts();
			ChannelServer.Instance.World.RemoveScriptedEntities();
			ChannelServer.Instance.World.SpawnManager.Clear();
			this.Load();
		}

		/// <summary>
		/// Returns path for the compiled version of the script.
		/// Creates directory structure if it doesn't exist.
		/// </summary>
		/// <param name="path"></param>
		/// <returns></returns>
		protected override string GetCachePath(string path)
		{
			path = path.Replace(Path.GetFullPath(SystemIndexRoot).Replace("\\", "/"), "");
			path = path.Replace(Path.GetFullPath(UserIndexRoot).Replace("\\", "/"), "");
			path = path.Replace(Path.GetFullPath(CacheRoot).Replace("\\", "/"), "");

			var result = Path.Combine(CacheRoot, base.GetCachePath(path));
			var dir = Path.GetDirectoryName(result);

			if (!Directory.Exists(dir))
				Directory.CreateDirectory(dir);

			return result;
		}

		/// <summary>
		/// Returns list of script files loaded from scripts.txt.
		/// </summary>
		/// <param name="scriptListFile"></param>
		/// <returns></returns>
		protected override List<string> ReadScriptList(string scriptListFile)
		{
			// Get original list
			var result = base.ReadScriptList(scriptListFile);

			// Fix paths to prioritize files in user over system
			var user = Path.GetFullPath(UserIndexRoot).Replace("\\", "/");
			var system = Path.GetFullPath(SystemIndexRoot).Replace("\\", "/");

			for (int i = 0; i < result.Count; ++i)
			{
				var path = result[i];
				path = path.Replace(user, "").Replace(system, "");

				if (File.Exists(Path.Combine(UserIndexRoot, path)))
					path = Path.Combine(UserIndexRoot, path).Replace("\\", "/");
				else
					path = Path.Combine(SystemIndexRoot, path).Replace("\\", "/");

				result[i] = path;
			}

			return result;
		}

		private void CreateInlineItemScriptFile()
		{
			// Place generated script in cache folder
			var outPath = this.GetCachePath(Path.Combine("system", "scripts", "items", "inline.generated.cs")).Replace(".compiled", "");

			// Check if db files were updated, if not we don't need to recreate
			// the inline script.
			var dbNewerThanScript =
				(File.GetLastWriteTime(Path.Combine("system", "db", "items.txt")) >= File.GetLastWriteTime(outPath)) ||
				(File.GetLastWriteTime(Path.Combine("user", "db", "items.txt")) >= File.GetLastWriteTime(outPath));

			if (!dbNewerThanScript)
				return;

			var sb = new StringBuilder();

			// Default usings
			sb.AppendLine("// Automatically generated from inline scripts in the item database");
			sb.AppendLine();
			sb.AppendLine("using Aura.Channel.World.Entities;");
			sb.AppendLine("using Aura.Channel.Scripting.Scripts;");
			sb.AppendLine();

			// Go through all items
			foreach (var entry in AuraData.ItemDb.Entries.Values)
			{
				var scriptsEmpty = (string.IsNullOrWhiteSpace(entry.OnUse) && string.IsNullOrWhiteSpace(entry.OnEquip) && string.IsNullOrWhiteSpace(entry.OnUnequip) && string.IsNullOrWhiteSpace(entry.OnCreation));

				if (scriptsEmpty)
					continue;

				sb.AppendFormat("// {0}: {1}" + Environment.NewLine, entry.Id, entry.Name);
				sb.AppendFormat("[ItemScript({0})]" + Environment.NewLine, entry.Id);
				sb.AppendFormat("public class ItemScript{0} : ItemScript {{" + Environment.NewLine, entry.Id);

				if (!string.IsNullOrWhiteSpace(entry.OnUse))
					sb.AppendFormat("	public override void OnUse(Creature cr, Item i)     {{ {0} }}" + Environment.NewLine, entry.OnUse.Trim());
				if (!string.IsNullOrWhiteSpace(entry.OnEquip))
					sb.AppendFormat("	public override void OnEquip(Creature cr, Item i)   {{ {0} }}" + Environment.NewLine, entry.OnEquip.Trim());
				if (!string.IsNullOrWhiteSpace(entry.OnUnequip))
					sb.AppendFormat("	public override void OnUnequip(Creature cr, Item i) {{ {0} }}" + Environment.NewLine, entry.OnUnequip.Trim());
				if (!string.IsNullOrWhiteSpace(entry.OnCreation))
					sb.AppendFormat("	public override void OnCreation(Item i) {{ {0} }}" + Environment.NewLine, entry.OnCreation.Trim());

				sb.AppendFormat("}}" + Environment.NewLine + Environment.NewLine);
			}

			File.WriteAllText(outPath, sb.ToString());
		}

		/// <summary>
		/// 5 min tick, global var saving.
		/// </summary>
		/// <param name="time"></param>
		public void OnMabiTick(ErinnTime time)
		{
			ChannelServer.Instance.Database.SaveVars("Aura System", 0, this.GlobalVars.Perm);
			Log.Info("Saved global script variables.");
		}
	}

	public delegate Task<HookResult> NpcScriptHook(NpcScript npc, params object[] args);
}
