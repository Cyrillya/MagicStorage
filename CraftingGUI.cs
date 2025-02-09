using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MagicStorage.Common;
using MagicStorage.Common.Systems;
using MagicStorage.Common.Systems.RecurrentRecipes;
using MagicStorage.Components;
using MagicStorage.CrossMod;
using MagicStorage.Items;
using MagicStorage.Sorting;
using MagicStorage.UI;
using MagicStorage.UI.States;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.Audio;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.UI;

namespace MagicStorage
{
	public static class CraftingGUI
	{
		public const int RecipeButtonsAvailableChoice = 0;
		//Button location could either be the third (2) or fourth (3) option depending on if the favoriting config is enabled
		public static int RecipeButtonsBlacklistChoice => MagicStorageConfig.CraftingFavoritingEnabled ? 3 : 2;
		public const int RecipeButtonsFavoritesChoice = 2;
		public const int Padding = 4;
		public const int RecipeColumns = 10;
		public const int IngredientColumns = 7;
		public const float InventoryScale = 0.85f;
		public const float SmallScale = 0.7f;
		public const int StartMaxCraftTimer = 20;
		public const int StartMaxRightClickTimer = 20;
		public const float ScrollBar2ViewSize = 1f;
		public const float RecipeScrollBarViewSize = 1f;

		internal static readonly List<Item> items = new();

		private static readonly Dictionary<int, int> itemCounts = new();
		internal static List<Recipe> recipes = new();
		internal static List<bool> recipeAvailable = new();
		internal static Recipe selectedRecipe;

		internal static bool slotFocus;

		internal static readonly List<Item> storageItems = new();
		internal static readonly List<bool> storageItemsFromModules = new();
		internal static readonly List<ItemData> blockStorageItems = new();
		internal static readonly List<List<Item>> sourceItems = new();
		// Only used by DoWithdrawResult to check items from modules
		private static readonly List<Item> sourceItemsFromModules = new();

		public static int craftAmountTarget;

		internal static Item result;
		internal static int craftTimer;
		internal static int maxCraftTimer = StartMaxCraftTimer;
		internal static int rightClickTimer;

		internal static int maxRightClickTimer = StartMaxRightClickTimer;

		[ThreadStatic]
		public static bool CatchDroppedItems;
		[ThreadStatic]
		public static List<Item> DroppedItems;

		private static bool[] adjTiles = new bool[TileLoader.TileCount];
		private static bool adjWater;
		private static bool adjLava;
		private static bool adjHoney;
		private static bool zoneSnow;
		private static bool alchemyTable;
		private static bool graveyard;
		public static bool Campfire { get; private set; }

		internal static void Unload()
		{
			selectedRecipe = null;
		}

		internal static void Reset() {
			Campfire = false;
			craftTimer = 0;
			maxCraftTimer = StartMaxCraftTimer;
			craftAmountTarget = 1;
		}

		internal static Item GetStation(int slot, ref int context)
		{
			List<Item> stations = GetCraftingStations();
			if (stations is not null && slot < stations.Count)
				return stations[slot];
			return new Item();
		}

		internal static Item GetHeader(int slot, ref int context)
		{
			return selectedRecipe?.createItem ?? new Item();
		}

		internal static Item GetIngredient(int slot, ref int context)
		{
			if (selectedRecipe == null || slot >= selectedRecipe.requiredItem.Count)
				return new Item();

			Item item = selectedRecipe.requiredItem[slot].Clone();
			if (selectedRecipe.HasRecipeGroup(RecipeGroupID.Wood) && item.type == ItemID.Wood)
				item.SetNameOverride(Language.GetText("LegacyMisc.37").Value + " " + Lang.GetItemNameValue(ItemID.Wood));
			if (selectedRecipe.HasRecipeGroup(RecipeGroupID.Sand) && item.type == ItemID.SandBlock)
				item.SetNameOverride(Language.GetText("LegacyMisc.37").Value + " " + Lang.GetItemNameValue(ItemID.SandBlock));
			if (selectedRecipe.HasRecipeGroup(RecipeGroupID.IronBar) && item.type == ItemID.IronBar)
				item.SetNameOverride(Language.GetText("LegacyMisc.37").Value + " " + Lang.GetItemNameValue(ItemID.IronBar));
			if (selectedRecipe.HasRecipeGroup(RecipeGroupID.Fragment) && item.type == ItemID.FragmentSolar)
				item.SetNameOverride(Language.GetText("LegacyMisc.37").Value + " " + Language.GetText("LegacyMisc.51").Value);
			if (selectedRecipe.HasRecipeGroup(RecipeGroupID.PressurePlate) && item.type == ItemID.GrayPressurePlate)
				item.SetNameOverride(Language.GetText("LegacyMisc.37").Value + " " + Language.GetText("LegacyMisc.38").Value);
			if (ProcessGroupsForText(selectedRecipe, item.type, out string nameOverride))
				item.SetNameOverride(nameOverride);

			int totalGroupStack = 0;
			Item storageItem = storageItems.FirstOrDefault(i => i.type == item.type) ?? new Item();

			foreach (RecipeGroup rec in selectedRecipe.acceptedGroups.Select(index => RecipeGroup.recipeGroups[index])) {
				if (rec.ValidItems.Contains(item.type)) {
					foreach (int type in rec.ValidItems)
						totalGroupStack += storageItems.Where(i => i.type == type).Sum(i => i.stack);
				}
			}

			if (!item.IsAir) {
				if (storageItem.IsAir && totalGroupStack == 0)
					context = ItemSlot.Context.ChestItem;  // Unavailable - Red
				else if (storageItem.stack < item.stack && totalGroupStack < item.stack)
					context = ItemSlot.Context.BankItem;  // Partially in stock - Pinkish

				// context == 0 - Available - Default Blue
				if (context != 0) {
					bool craftable;

					using (FlagSwitch.ToggleTrue(ref disableNetPrintingForIsAvailable)) {
						// Forcibly prevent any subrecipes using this item type from being "available"
						craftable = MagicCache.ResultToRecipe.TryGetValue(item.type, out var r) && r.Any(recipe => IsAvailable(recipe, true, selectedRecipe.createItem.type));
					}

					if (craftable)
						context = ItemSlot.Context.TrashItem;  // Craftable - Light green
				}
			}

			return item;
		}

		internal static bool ProcessGroupsForText(Recipe recipe, int type, out string theText)
		{
			foreach (int num in recipe.acceptedGroups)
				if (RecipeGroup.recipeGroups[num].ContainsItem(type))
				{
					theText = RecipeGroup.recipeGroups[num].GetText();
					return true;
				}

			theText = "";
			return false;
		}

		private static int? amountCraftableForCurrentRecipe;
		private static Recipe recentRecipeAmountCraftable;

		public static int AmountCraftableForCurrentRecipe() {
			if (currentlyThreading || StorageGUI.CurrentlyRefreshing)
				return 0;  // Delay logic until threading stops

			if (object.ReferenceEquals(recentRecipeAmountCraftable, selectedRecipe) && amountCraftableForCurrentRecipe is { } amount)
				return amount;

			// Calculate the value
			recentRecipeAmountCraftable = selectedRecipe;
			amountCraftableForCurrentRecipe = amount = AmountCraftable(selectedRecipe);
			return amount;
		}

		internal static bool requestingAmountFromUI;

		// Calculates how many times a recipe can be crafted using available items
		internal static int AmountCraftable(Recipe recipe)
		{
			NetHelper.Report(true, "Calculating maximum amount to craft for current recipe...");

			if (MagicStorageConfig.IsRecursionEnabled && recipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe)) {
				NetHelper.Report(false, "Recipe had a recursion tree");

				using (FlagSwitch.ToggleTrue(ref requestingAmountFromUI))
					return recursiveRecipe.GetMaxCraftable(GetCurrentInventory(cloneIfBlockEmpty: true));
			}

			NetHelper.Report(false, "Recipe did not hae a recursion tree or recursion was disabled");

			// Handle the old logic
			if (!IsAvailable(recipe))
				return 0;

			// Local capturing
			Recipe r = recipe;

			int GetMaxCraftsAmount(Item requiredItem) {
				ClampedArithmetic total = 0;
				foreach (Item inventoryItem in items) {
					if (inventoryItem.type == requiredItem.type || RecipeGroupMatch(r, inventoryItem.type, requiredItem.type))
						total += inventoryItem.stack;
				}

				int craftable = total / requiredItem.stack;
				return craftable;
			}

			int maxCrafts = recipe.requiredItem.Select(GetMaxCraftsAmount).Prepend(9999).Min() * recipe.createItem.stack;

			if ((uint)maxCrafts > 9999)
				maxCrafts = 9999;

			NetHelper.Report(false, $"Possible crafts = {maxCrafts}");

			return maxCrafts;
		}

		internal static Item GetResult(int slot, ref int context) => slot == 0 && result is not null ? result : new Item();

		internal static void ClickCraftButton(ref bool stillCrafting) {
			if (craftTimer <= 0)
			{
				craftTimer = maxCraftTimer;
				maxCraftTimer = maxCraftTimer * 3 / 4;
				if (maxCraftTimer <= 0)
					maxCraftTimer = 1;

				int amount = craftAmountTarget;

				if (MagicStorageConfig.UseOldCraftMenu && Main.keyState.IsKeyDown(Keys.LeftControl))
					amount = 9999;

				Craft(amount);

				IEnumerable<int> allItemTypes = selectedRecipe.requiredItem.Select(i => i.type).Prepend(selectedRecipe.createItem.type);

				//If no recipes were affected, that's fine, none of the recipes will be touched due to the calulated Recipe array being empty
				SetNextDefaultRecipeCollectionToRefresh(allItemTypes);
				StorageGUI.SetRefresh();
				SoundEngine.PlaySound(SoundID.Grab);
			}

			craftTimer--;
			stillCrafting = true;
		}

		internal static void ClickAmountButton(int amount, bool offset) {
			if (StorageGUI.CurrentlyRefreshing)
				return;  // Do not read anything until refreshing is completed

			int oldTarget = craftAmountTarget;
			if (offset && (amount == 1 || craftAmountTarget > 1))
				craftAmountTarget += amount;
			else
				craftAmountTarget = amount;  //Snap directly to the amount if the amount target was 1 (this makes clicking 10 when at 1 just go to 10 instead of 11)

			using (FlagSwitch.ToggleFalse(ref clampCraftAmountAllowCacheReset))
				ClampCraftAmount();

			if (craftAmountTarget != oldTarget) {
				ResetCachedBlockedIngredientsCheck();
				ResetCachedCraftingSimulation();

				if (MagicStorageConfig.IsRecursionEnabled) {
					RefreshStorageItems();
					StorageGUI.SetRefresh();
				}
			}

			SoundEngine.PlaySound(SoundID.MenuTick);
		}

		private static bool clampCraftAmountAllowCacheReset;
		internal static void ClampCraftAmount() {
			if (StorageGUI.CurrentlyRefreshing)
				return;  // Recipe/ingredient information may not be available

			int oldTarget = craftAmountTarget;

			if (craftAmountTarget < 1 || selectedRecipe is null || selectedRecipe.createItem.maxStack == 1 || !IsCurrentRecipeFullyAvailable())
				craftAmountTarget = 1;
			else {
				int amountCraftable = AmountCraftableForCurrentRecipe();
				int max = Utils.Clamp(amountCraftable, 1, selectedRecipe.createItem.maxStack);

				if (craftAmountTarget > max)
					craftAmountTarget = max;
			}

			if (clampCraftAmountAllowCacheReset && oldTarget != craftAmountTarget) {
				ResetCachedBlockedIngredientsCheck();
				ResetCachedCraftingSimulation();

				if (MagicStorageConfig.IsRecursionEnabled) {
					RefreshStorageItems();
					StorageGUI.SetRefresh();
				}
			}
		}

		internal static TEStorageHeart GetHeart() => StoragePlayer.LocalPlayer.GetStorageHeart();

		internal static TECraftingAccess GetCraftingEntity() => StoragePlayer.LocalPlayer.GetCraftingAccess();

		internal static List<Item> GetCraftingStations() => GetCraftingEntity()?.stations ?? new();

		public static void RefreshItems() => RefreshItemsAndSpecificRecipes(null);

		private static int numItemsWithoutSimulators;
		private static int numSimulatorItems;

		private class ThreadState {
			public EnvironmentSandbox sandbox;
			public Recipe[] recipesToRefresh;
			public IEnumerable<Item> heartItems;
			public IEnumerable<Item> simulatorItems;
			public ItemTypeOrderedSet hiddenRecipes, favoritedRecipes;
			public int recipeFilterChoice;
			public bool[] recipeConditionsMetSnapshot;
		}

		private static bool currentlyThreading;
		internal static Recipe[] recipesToRefresh;

		/// <summary>
		/// Adds <paramref name="recipes"/> to the collection of recipes to refresh when calling <see cref="RefreshItems"/>
		/// </summary>
		/// <param name="recipes">An array of recipes to update.  If <see langword="null"/>, then nothing happens</param>
		public static void SetNextDefaultRecipeCollectionToRefresh(Recipe[] recipes) {
			if (recipesToRefresh is null) {
				if (recipes is not null) {
					recipes = ExpandRecipeCollectionWithPossibleRecursionDependents(recipes).ToArray();

					NetHelper.Report(true, $"Setting next refresh to check {recipes.Length} recipes");
				}

				recipesToRefresh = recipes;
				return;
			}

			if (recipes is null)
				return;

			var updatedList = recipesToRefresh.Concat(recipes);
			updatedList = ExpandRecipeCollectionWithPossibleRecursionDependents(updatedList);

			recipesToRefresh = updatedList.DistinctBy(static r => r, ReferenceEqualityComparer.Instance).ToArray();

			NetHelper.Report(true, $"Setting next refresh to check {recipesToRefresh.Length} recipes");
		}

		private static IEnumerable<Recipe> ExpandRecipeCollectionWithPossibleRecursionDependents(IEnumerable<Recipe> toRefresh) {
			if (!MagicStorageConfig.IsRecursionEnabled)
				return toRefresh;

			return toRefresh.Concat(toRefresh.SelectMany(static r => MagicCache.RecursiveRecipesUsingRecipeByIndex.TryGetValue(r.RecipeIndex, out var recipes)
				? recipes.Select(static node => node.info.sourceRecipe)
				: Array.Empty<Recipe>()));
		}

		/// <summary>
		/// Adds all recipes which use <paramref name="affectedItemType"/> as an ingredient or result to the collection of recipes to refresh when calling <see cref="RefreshItems"/>
		/// </summary>
		/// <param name="affectedItemType">The item type to use when checking <see cref="MagicCache.RecipesUsingItemType"/></param>
		public static void SetNextDefaultRecipeCollectionToRefresh(int affectedItemType) {
			SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingItemType.TryGetValue(affectedItemType, out var result) ? result.Value : null);
		}

		/// <summary>
		/// Adds all recipes which use the IDs in <paramref name="affectedItemTypes"/> as an ingredient or result to the collection of recipes to refresh when calling <see cref="RefreshItems"/>
		/// </summary>
		/// <param name="affectedItemTypes">A collection of item types to use when checking <see cref="MagicCache.RecipesUsingItemType"/></param>
		public static void SetNextDefaultRecipeCollectionToRefresh(IEnumerable<int> affectedItemTypes) {
			SetNextDefaultRecipeCollectionToRefresh(affectedItemTypes.SelectMany(static i => MagicCache.RecipesUsingItemType.TryGetValue(i, out var result) ? result.Value : Array.Empty<Recipe>())
				.DistinctBy(static r => r, ReferenceEqualityComparer.Instance)
				.ToArray());
		}

		/// <summary>
		/// Adds all recipes which use <paramref name="affectedTileType"/> as a required tile to the collection of recipes to refresh when calling <see cref="RefreshItems"/>
		/// </summary>
		/// <param name="affectedTileType">The tile type to use when checking <see cref="MagicCache.RecipesUsingTileType"/></param>
		public static void SetNextDefaultRecipeCollectionToRefreshFromTile(int affectedTileType) {
			SetNextDefaultRecipeCollectionToRefresh(MagicCache.RecipesUsingTileType.TryGetValue(affectedTileType, out var result) ? result.Value : null);
		}

		/// <summary>
		/// Adds all recipes which the IDs in <paramref name="affectedTileTypes"/> as a required tile to the collection of recipes to refresh when calling <see cref="RefreshItems"/>
		/// </summary>
		/// <param name="affectedTileTypes">A collection of the tile type to use when checking <see cref="MagicCache.RecipesUsingTileType"/></param>
		public static void SetNextDefaultRecipeCollectionToRefreshFromTile(IEnumerable<int> affectedTileTypes) {
			SetNextDefaultRecipeCollectionToRefresh(affectedTileTypes.SelectMany(static t => MagicCache.RecipesUsingTileType.TryGetValue(t, out var result) ? result.Value : Array.Empty<Recipe>())
				.DistinctBy(static r => r, ReferenceEqualityComparer.Instance)
				.ToArray());
		}

		private static void RefreshItemsAndSpecificRecipes(Recipe[] toRefresh) {
			if (!StorageGUI.ForceNextRefreshToBeFull) {
				// Custom array provided?  Refresh the default array anyway
				SetNextDefaultRecipeCollectionToRefresh(toRefresh);
				toRefresh = recipesToRefresh;
			} else {
				// Force all recipes to be recalculated
				recipesToRefresh = null;
				toRefresh = null;
			}

			var craftingPage = MagicUI.craftingUI.GetPage<CraftingUIState.RecipesPage>("Crafting");

			craftingPage?.RequestThreadWait(waiting: true);

			if (StorageGUI.CurrentlyRefreshing) {
				StorageGUI.activeThread?.Stop();
				StorageGUI.activeThread = null;
			}

			// Always reset the cached values
			ResetRecentRecipeCache();

			items.Clear();
			sourceItems.Clear();
			sourceItemsFromModules.Clear();
			numItemsWithoutSimulators = 0;
			TEStorageHeart heart = GetHeart();
			if (heart == null) {
				craftingPage?.RequestThreadWait(waiting: false);

				StorageGUI.InvokeOnRefresh();
				return;
			}

			NetHelper.Report(true, "CraftingGUI: RefreshItemsAndSpecificRecipes invoked");

			EnvironmentSandbox sandbox = new(Main.LocalPlayer, heart);

			foreach (var module in heart.GetModules())
				module.PreRefreshRecipes(sandbox);

			StorageGUI.CurrentlyRefreshing = true;

			IEnumerable<Item> heartItems = heart.GetStoredItems();
			IEnumerable<Item> simulatorItems = heart.GetModules().SelectMany(m => m.GetAdditionalItems(sandbox) ?? Array.Empty<Item>())
				.Where(i => i.type > ItemID.None && i.stack > 0)
				.DistinctBy(i => i, ReferenceEqualityComparer.Instance);  //Filter by distinct object references (prevents "duplicate" items from, say, 2 mods adding items from the player's inventory)

			int sortMode = MagicUI.craftingUI.GetPage<SortingPage>("Sorting").option;
			int filterMode = MagicUI.craftingUI.GetPage<FilteringPage>("Filtering").option;

			var recipesPage = MagicUI.craftingUI.GetPage<CraftingUIState.RecipesPage>("Crafting");
			string searchText = recipesPage.searchBar.Text;

			var hiddenRecipes = StoragePlayer.LocalPlayer.HiddenRecipes;
			var favorited = StoragePlayer.LocalPlayer.FavoritedRecipes;

			int recipeChoice = recipesPage.recipeButtons.Choice;
			int modSearchIndex = recipesPage.modSearchBox.ModIndex;

			ThreadState state;
			StorageGUI.ThreadContext thread = new(new CancellationTokenSource(), SortAndFilter, AfterSorting) {
				heart = heart,
				sortMode = sortMode,
				filterMode = filterMode,
				searchText = searchText,
				onlyFavorites = false,
				modSearch = modSearchIndex,
				state = state = new ThreadState() {
					sandbox = sandbox,
					recipesToRefresh = toRefresh,
					heartItems = heartItems,
					simulatorItems = simulatorItems,
					hiddenRecipes = hiddenRecipes,
					favoritedRecipes = favorited,
					recipeFilterChoice = recipeChoice
				}
			};

			// Update the adjacent tiles and condition contexts
			AnalyzeIngredients();

			ExecuteInCraftingGuiEnvironment(() => {
				state.recipeConditionsMetSnapshot = Main.recipe.Take(Recipe.maxRecipes).Select(static r => !r.Disabled && RecipeLoader.RecipeAvailable(r)).ToArray();
			});

			if (heart is not null) {
				foreach (EnvironmentModule module in heart.GetModules())
					module.ResetPlayer(sandbox);
			}

			StorageGUI.ThreadContext.Begin(thread);
		}

		private static void SortAndFilter(StorageGUI.ThreadContext thread) {
			currentlyThreading = true;

			currentRecipeIsAvailable = null;

			if (thread.state is ThreadState state) {
				LoadStoredItems(thread, state);
				RefreshStorageItems();
				
				try {
					SafelyRefreshRecipes(thread, state);
				} catch when (thread.token.IsCancellationRequested) {
					recipes.Clear();
					recipeAvailable.Clear();
					throw;
				}
			}

			currentlyThreading = false;
			recipesToRefresh = null;
		}

		private static void AfterSorting(StorageGUI.ThreadContext thread) {
			// Refresh logic in the UIs will only run when this is false
			if (!thread.token.IsCancellationRequested)
				StorageGUI.CurrentlyRefreshing = false;

			// Ensure that race conditions with the UI can't occur
			// QueueMainThreadAction will execute the logic in a very specific place
			Main.QueueMainThreadAction(StorageGUI.InvokeOnRefresh);

			var sandbox = (thread.state as ThreadState).sandbox;

			foreach (var module in thread.heart.GetModules())
				module.PostRefreshRecipes(sandbox);

			NetHelper.Report(true, "CraftingGUI: RefreshItemsAndSpecificRecipes finished");

			MagicUI.craftingUI.GetPage<CraftingUIState.RecipesPage>("Crafting")?.RequestThreadWait(waiting: false);
		}

		private static void LoadStoredItems(StorageGUI.ThreadContext thread, ThreadState state) {
			try {
				// Task count: loading simulator items, 5 tasks from SortAndFilter, adding full item list, adding module items to source, updating source items list, updating counts dictionary
				thread.InitTaskSchedule(10, "Loading items");

				var clone = thread.Clone(
					newSortMode: SortingOptionLoader.Definitions.ID.Type,
					newFilterMode: FilteringOptionLoader.Definitions.All.Type,
					newSearchText: "",
					newModSearch: ModSearchBox.ModIndexAll);

				thread.context = clone.context = new(state.simulatorItems);

				items.AddRange(ItemSorter.SortAndFilter(clone, aggregate: false));

				thread.CompleteOneTask();

				numSimulatorItems = items.Count;

				var simulatorItems = thread.context.sourceItems;

				// Prepend the heart items before the module items
				NetHelper.Report(true, "Loading stored items from storage system...");

				clone.context = new(state.heartItems);

				var prependedItems = ItemSorter.SortAndFilter(clone).Concat(items).ToList();

				items.Clear();
				items.AddRange(prependedItems);

				thread.CompleteOneTask();

				numItemsWithoutSimulators = items.Count - numSimulatorItems;

				var moduleItems = simulatorItems.ToList();

				sourceItems.AddRange(clone.context.sourceItems.Concat(moduleItems));

				thread.CompleteOneTask();

				sourceItemsFromModules.AddRange(moduleItems.SelectMany(static list => list));

				thread.CompleteOneTask();

				// Context no longer needed
				thread.context = null;

				NetHelper.Report(false, "Total items: " + items.Count);
				NetHelper.Report(false, "Items from modules: " + numSimulatorItems);

				itemCounts.Clear();
				foreach ((int type, int amount) in items.GroupBy(item => item.type, item => item.stack, (type, stacks) => (type, stacks.ConstrainedSum())))
					itemCounts[type] = amount;

				thread.CompleteOneTask();
			} catch when (thread.token.IsCancellationRequested) {
				items.Clear();
				numItemsWithoutSimulators = 0;
				sourceItems.Clear();
				sourceItemsFromModules.Clear();
				itemCounts.Clear();
				throw;
			}
		}

		private static void SafelyRefreshRecipes(StorageGUI.ThreadContext thread, ThreadState state) {
			try {
				if (state.recipesToRefresh is null)
					RefreshRecipes(thread, state);  //Refresh all recipes
				else {
					RefreshSpecificRecipes(thread, state);

					forceSpecificRecipeResort = false;

					// Do a second pass when recursion crafting is enabled
					if (MagicStorageConfig.IsRecursionEnabled) {
						state.recipesToRefresh = recipes.ToArray();
						RefreshSpecificRecipes(thread, state);
					}
				}

				NetHelper.Report(false, "Visible recipes: " + recipes.Count);
				NetHelper.Report(false, "Available recipes: " + recipeAvailable.Count(static b => b));

				NetHelper.Report(true, "Recipe refreshing finished");
			} catch (Exception e) {
				Main.QueueMainThreadAction(() => Main.NewTextMultiline(e.ToString(), c: Color.White));
			}
		}

		private static void RefreshRecipes(StorageGUI.ThreadContext thread, ThreadState state)
		{
			NetHelper.Report(true, "Refreshing all recipes");

			// Each DoFiltering does: GetRecipes, SortRecipes, adding recipes, adding recipe availability
			// Each GetRecipes does: loading base recipes, applying text/mod filters
			// Each SortRecipes does: DoSorting, blacklist filtering, favorite checks

			thread.InitTaskSchedule(9, "Refreshing recipes");

			DoFiltering(thread, state);

			bool didDefault = false;

			// now if nothing found we disable filters one by one
			if (thread.searchText.Length > 0)
			{
				if (recipes.Count == 0 && state.hiddenRecipes.Count > 0)
				{
					NetHelper.Report(true, "No recipes passed the filter.  Attempting filter with no hidden recipes");

					// search hidden recipes too
					state.hiddenRecipes = ItemTypeOrderedSet.Empty;

					MagicUI.lastKnownSearchBarErrorReason = Language.GetTextValue("Mods.MagicStorage.Warnings.CraftingNoBlacklist");
					didDefault = true;

					thread.ResetTaskCompletion();

					DoFiltering(thread, state);
				}

				/*
				if (recipes.Count == 0 && filterMode != FilterMode.All)
				{
					// any category
					filterMode = FilterMode.All;
					DoFiltering(sortMode, filterMode, hiddenRecipes, favorited);
				}
				*/

				if (recipes.Count == 0 && thread.modSearch != ModSearchBox.ModIndexAll)
				{
					NetHelper.Report(true, "No recipes passed the filter.  Attempting filter with All Mods setting");

					// search all mods
					thread.modSearch = ModSearchBox.ModIndexAll;

					MagicUI.lastKnownSearchBarErrorReason = Language.GetTextValue("Mods.MagicStorage.Warnings.CraftingDefaultToAllMods");
					didDefault = true;

					thread.ResetTaskCompletion();

					DoFiltering(thread, state);
				}
			}

			if (!didDefault)
				MagicUI.lastKnownSearchBarErrorReason = null;
		}

		private static void DoFiltering(StorageGUI.ThreadContext thread, ThreadState state)
		{
			try {
				NetHelper.Report(true, "Retrieving recipes...");

				var filteredRecipes = ItemSorter.GetRecipes(thread);

				thread.CompleteOneTask();

				NetHelper.Report(true, "Sorting recipes...");

				IEnumerable<Recipe> sortedRecipes = SortRecipes(thread, state, filteredRecipes);

				thread.CompleteOneTask();

				recipes.Clear();
				recipeAvailable.Clear();
				
				// For some reason, the loading text likes to hide itself here...
				MagicUI.craftingUI.GetPage<CraftingUIState.RecipesPage>("Crafting")?.RequestThreadWait(waiting: true);

				using (FlagSwitch.ToggleTrue(ref disableNetPrintingForIsAvailable)) {
					if (state.recipeFilterChoice == RecipeButtonsAvailableChoice)
					{
						NetHelper.Report(true, "Filtering out only available recipes...");

						recipes.AddRange(sortedRecipes.Where(r => IsAvailable(r)));

						thread.CompleteOneTask();

						recipeAvailable.AddRange(Enumerable.Repeat(true, recipes.Count));

						thread.CompleteOneTask();
					}
					else
					{
						recipes.AddRange(sortedRecipes);

						thread.CompleteOneTask();

						recipeAvailable.AddRange(recipes.AsParallel().AsOrdered().Select(r => IsAvailable(r)));

						thread.CompleteOneTask();
					}
				}

				// For some reason, the loading text likes to hide itself here...
				MagicUI.craftingUI.GetPage<CraftingUIState.RecipesPage>("Crafting")?.RequestThreadWait(waiting: true);
			} catch when (thread.token.IsCancellationRequested) {
				recipes.Clear();
				recipeAvailable.Clear();
			}
		}

		internal static bool forceSpecificRecipeResort;

		private static void RefreshSpecificRecipes(StorageGUI.ThreadContext thread, ThreadState state) {
			NetHelper.Report(true, "Refreshing " + state.recipesToRefresh.Length + " recipes");

			// Task count: N recipes, 3 tasks from SortRecipes, adding recipes, adding recipe availability
			thread.InitTaskSchedule(state.recipesToRefresh.Length + 5, $"Refreshing {state.recipesToRefresh.Length} recipes");

			//Assumes that the recipes are visible in the GUI
			bool needsResort = forceSpecificRecipeResort;

			foreach (Recipe recipe in state.recipesToRefresh) {
				if (recipe is null) {
					thread.CompleteOneTask();
					continue;
				}

				if (!ItemSorter.RecipePassesFilter(recipe, thread)) {
					thread.CompleteOneTask();
					continue;
				}

				int index = recipes.IndexOf(recipe);

				using (FlagSwitch.ToggleTrue(ref disableNetPrintingForIsAvailable)) {
					if (!IsAvailable(recipe)) {
						if (index >= 0) {
							if (state.recipeFilterChoice == RecipeButtonsAvailableChoice) {
								//Remove the recipe
								recipes.RemoveAt(index);
								recipeAvailable.RemoveAt(index);
							} else {
								//Simply mark the recipe as unavailable
								recipeAvailable[index] = false;
							}
						}
					} else {
						if (state.recipeFilterChoice == RecipeButtonsAvailableChoice) {
							if (index < 0 && CanBeAdded(thread, state, recipe)) {
								//Add the recipe
								recipes.Add(recipe);
								needsResort = true;
							}
						} else {
							if (index >= 0) {
								//Simply mark the recipe as available
								recipeAvailable[index] = true;
							}
						}
					}
				}

				thread.CompleteOneTask();
			}

			if (needsResort) {
				var sorted = new List<Recipe>(recipes)
					.AsParallel()
					.AsOrdered();

				IEnumerable<Recipe> sortedRecipes = SortRecipes(thread, state, sorted);

				recipes.Clear();
				recipeAvailable.Clear();

				recipes.AddRange(sortedRecipes);

				thread.CompleteOneTask();

				recipeAvailable.AddRange(Enumerable.Repeat(true, recipes.Count));

				thread.CompleteOneTask();
			}
		}

		private static bool CanBeAdded(StorageGUI.ThreadContext thread, ThreadState state, Recipe r) => Array.IndexOf(MagicCache.FilteredRecipesCache[thread.filterMode], r) >= 0
			&& ItemSorter.FilterBySearchText(r.createItem, thread.searchText, thread.modSearch)
			// show only blacklisted recipes only if choice = 2, otherwise show all other
			&& (!MagicStorageConfig.RecipeBlacklistEnabled || state.recipeFilterChoice == RecipeButtonsBlacklistChoice == state.hiddenRecipes.Contains(r.createItem))
			// show only favorited items if selected
			&& (!MagicStorageConfig.CraftingFavoritingEnabled || state.recipeFilterChoice != RecipeButtonsFavoritesChoice || state.favoritedRecipes.Contains(r.createItem));

		private static IEnumerable<Recipe> SortRecipes(StorageGUI.ThreadContext thread, ThreadState state, IEnumerable<Recipe> source) {
			IEnumerable<Recipe> sortedRecipes = ItemSorter.DoSorting(thread, source, r => r.createItem);

			thread.CompleteOneTask();

			// show only blacklisted recipes only if choice = 2, otherwise show all other
			if (MagicStorageConfig.RecipeBlacklistEnabled)
				sortedRecipes = sortedRecipes.Where(x => state.recipeFilterChoice == RecipeButtonsBlacklistChoice == state.hiddenRecipes.Contains(x.createItem));

			thread.CompleteOneTask();

			// favorites first
			if (MagicStorageConfig.CraftingFavoritingEnabled) {
				sortedRecipes = sortedRecipes.Where(x => state.recipeFilterChoice != RecipeButtonsFavoritesChoice || state.favoritedRecipes.Contains(x.createItem));
					
				sortedRecipes = sortedRecipes.OrderByDescending(r => state.favoritedRecipes.Contains(r.createItem) ? 1 : 0);
			}

			thread.CompleteOneTask();

			return sortedRecipes;
		}

		private static void AnalyzeIngredients()
		{
			NetHelper.Report(true, "Analyzing crafting stations and environment requirements...");

			Player player = Main.LocalPlayer;
			if (adjTiles.Length != player.adjTile.Length)
				Array.Resize(ref adjTiles, player.adjTile.Length);

			Array.Clear(adjTiles, 0, adjTiles.Length);
			adjWater = false;
			adjLava = false;
			adjHoney = false;
			zoneSnow = false;
			alchemyTable = false;
			graveyard = false;
			Campfire = false;

			foreach (Item item in GetCraftingStations())
			{
				if (item.IsAir)
					continue;

				if (item.createTile >= TileID.Dirt)
				{
					adjTiles[item.createTile] = true;
					switch (item.createTile)
					{
						case TileID.GlassKiln:
						case TileID.Hellforge:
							adjTiles[TileID.Furnaces] = true;
							break;
						case TileID.AdamantiteForge:
							adjTiles[TileID.Furnaces] = true;
							adjTiles[TileID.Hellforge] = true;
							break;
						case TileID.MythrilAnvil:
							adjTiles[TileID.Anvils] = true;
							break;
						case TileID.BewitchingTable:
						case TileID.Tables2:
							adjTiles[TileID.Tables] = true;
							break;
						case TileID.AlchemyTable:
							adjTiles[TileID.Bottles] = true;
							adjTiles[TileID.Tables] = true;
							alchemyTable = true;
							break;
					}

					if (item.createTile == TileID.Tombstones)
					{
						adjTiles[TileID.Tombstones] = true;
						graveyard = true;
					}

					bool[] oldAdjTile = (bool[])player.adjTile.Clone();
					bool oldAdjWater = adjWater;
					bool oldAdjLava = adjLava;
					bool oldAdjHoney = adjHoney;
					bool oldAlchemyTable = alchemyTable;
					player.adjTile = adjTiles;
					player.adjWater = false;
					player.adjLava = false;
					player.adjHoney = false;
					player.alchemyTable = false;

					TileLoader.AdjTiles(player, item.createTile);

					if (player.adjTile[TileID.WorkBenches] || player.adjTile[TileID.Tables] || player.adjTile[TileID.Tables2])
						player.adjTile[TileID.Chairs] = true;
					if (player.adjWater || TileID.Sets.CountsAsWaterSource[item.createTile])
						adjWater = true;
					if (player.adjLava || TileID.Sets.CountsAsLavaSource[item.createTile])
						adjLava = true;
					if (player.adjHoney || TileID.Sets.CountsAsHoneySource[item.createTile])
						adjHoney = true;
					if (player.alchemyTable || player.adjTile[TileID.AlchemyTable])
						alchemyTable = true;
					if (player.adjTile[TileID.Tombstones])
						graveyard = true;

					player.adjTile = oldAdjTile;
					player.adjWater = oldAdjWater;
					player.adjLava = oldAdjLava;
					player.adjHoney = oldAdjHoney;
					player.alchemyTable = oldAlchemyTable;
				}

				switch (item.type)
				{
					case ItemID.WaterBucket:
					case ItemID.BottomlessBucket:
						adjWater = true;
						break;
					case ItemID.LavaBucket:
					case ItemID.BottomlessLavaBucket:
						adjLava = true;
						break;
					case ItemID.HoneyBucket:
						adjHoney = true;
						break;
				}
				if (item.type == ModContent.ItemType<SnowBiomeEmulator>())
				{
					zoneSnow = true;
				}

				if (item.type == ModContent.ItemType<BiomeGlobe>())
				{
					zoneSnow = true;
					graveyard = true;
					Campfire = true;
					adjWater = true;
					adjLava = true;
					adjHoney = true;

					adjTiles[TileID.Campfire] = true;
					adjTiles[TileID.DemonAltar] = true;
				}
			}

			adjTiles[ModContent.TileType<Components.CraftingAccess>()] = true;

			TEStorageHeart heart = GetHeart();
			EnvironmentSandbox sandbox = new(player, heart);
			CraftingInformation information = new(Campfire, zoneSnow, graveyard, adjWater, adjLava, adjHoney, alchemyTable, adjTiles);

			if (heart is not null) {
				foreach (EnvironmentModule module in heart.GetModules())
					module.ModifyCraftingZones(sandbox, ref information);
			}

			Campfire = information.campfire;
			zoneSnow = information.snow;
			graveyard = information.graveyard;
			adjWater = information.water;
			adjLava = information.lava;
			adjHoney = information.honey;
			alchemyTable = information.alchemyTable;
			adjTiles = information.adjTiles;
		}

		private static bool? currentRecipeIsAvailable;
		private static Recipe recentRecipeAvailable;

		/// <summary>
		/// Returns <see langword="true"/> if the current recipe is available and passes the "blocked ingredients" filter
		/// </summary>
		public static bool IsCurrentRecipeFullyAvailable() => IsCurrentRecipeAvailable() && DoesCurrentRecipePassIngredientBlock();

		public static bool IsCurrentRecipeAvailable() {
			if (currentlyThreading || StorageGUI.CurrentlyRefreshing)
				return false;  // Delay logic until threading stops

			if (object.ReferenceEquals(recentRecipeAvailable, selectedRecipe) && currentRecipeIsAvailable is { } available)
				return available;

			// Calculate the value
			recentRecipeAvailable = selectedRecipe;
			currentRecipeIsAvailable = available = IsAvailable(selectedRecipe) && PassesBlock(selectedRecipe);
			return available;
		}

		private static bool? currentRecipePassesBlock;
		private static Recipe recentRecipeBlock;

		public static bool DoesCurrentRecipePassIngredientBlock() {
			if (currentlyThreading || StorageGUI.CurrentlyRefreshing)
				return false;  // Delay logic until threading stops

			if (object.ReferenceEquals(recentRecipeBlock, selectedRecipe) && currentRecipePassesBlock is { } available)
				return available;

			// Calculate the value
			recentRecipeBlock = selectedRecipe;
			currentRecipePassesBlock = available = PassesBlock(selectedRecipe);
			return available;
		}

		public static void ResetCachedBlockedIngredientsCheck() {
			recentRecipeBlock = null;
			currentRecipePassesBlock = null;
		}

		public static void ResetRecentRecipeCache() {
			recentRecipeAvailable = null;
			currentRecipeIsAvailable = null;
			recentRecipeBlock = null;
			currentRecipePassesBlock = null;
			recentRecipeAmountCraftable = null;
			amountCraftableForCurrentRecipe = null;
			recentRecipeSimulation = null;
			simulatedCraftForCurrentRecipe = null;
		}

		/// <summary>
		/// Returns the recursion crafting tree for <paramref name="recipe"/> if it exists and recursion is enabled, or <see langword="null"/> otherwise.
		/// </summary>
		/// <param name="recipe">The recipe</param>
		/// <param name="toCraft">The quantity of the final recipe's crafted item to create</param>
		/// <param name="blockedSubrecipeIngredient">An optional item ID representing ingredient trees that should be ignored</param>
		public static OrderedRecipeTree GetCraftingTree(Recipe recipe, int toCraft = 1, int blockedSubrecipeIngredient = 0) {
			if (!MagicStorageConfig.IsRecursionEnabled || !recipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe))
				return null;

			return recursiveRecipe.GetCraftingTree(toCraft, available: GetCurrentInventory(), blockedSubrecipeIngredient);
		}

		internal static bool disableNetPrintingForIsAvailable;

		public static bool IsAvailable(Recipe recipe, bool checkRecursive = true) => IsAvailable(recipe, checkRecursive, recipe?.createItem.type ?? 0);

		private static bool IsAvailable(Recipe recipe, bool checkRecursive, int ignoreItem)
		{
			if (recipe is null)
				return false;

			if (!disableNetPrintingForIsAvailable) {
				NetHelper.Report(true, "Checking if recipe is available...");

				if (checkRecursive && MagicStorageConfig.IsRecursionEnabled)
					NetHelper.Report(false, "Calculating recursion tree for recipe...");
			}

			bool available = false;
			if (checkRecursive && MagicStorageConfig.IsRecursionEnabled && recipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe)) {
				if (currentlyThreading)
					available = IsAvailable_CheckRecursiveRecipe(recursiveRecipe, ignoreItem);
				else {
					ExecuteInCraftingGuiEnvironment(() => {
						available = IsAvailable_CheckRecursiveRecipe(recursiveRecipe, ignoreItem);
					});
				}
			} else
				available = IsAvailable_CheckNormalRecipe(recipe);

			if (!disableNetPrintingForIsAvailable)
				NetHelper.Report(true, $"Recipe {(available ? "was" : "was not")} available");

			return available;
		}

		private static bool IsAvailable_CheckRecursiveRecipe(RecursiveRecipe recipe, int ignoreItem) {
			var availableObjects = GetCurrentInventory(cloneIfBlockEmpty: true);
			if (ignoreItem > 0)
				availableObjects.RemoveIngredient(ignoreItem);

			using (FlagSwitch.ToggleTrue(ref requestingAmountFromUI)) {
				CraftingSimulation simulation = new CraftingSimulation();
				simulation.SimulateCrafts(recipe, 1, availableObjects);  // Recipe is available if at least one craft is possible
				return simulation.AmountCrafted > 0;
			}
		}

		private static bool IsAvailable_CheckNormalRecipe(Recipe recipe, int batches = 1) {
			if (recipe is null)
				return false;

			if (recipe.requiredTile.Any(tile => !adjTiles[tile]))
				return false;

			var itemCountsDictionary = GetItemCountsWithBlockedItemsRemoved();

			foreach (Item ingredient in recipe.requiredItem)
			{
				if (ingredient.stack * batches - IsAvailable_GetItemCount(recipe, ingredient.type, itemCountsDictionary) > 0)
					return false;
			}

			if (currentlyThreading)
				return StorageGUI.activeThread.state is ThreadState state && state.recipeConditionsMetSnapshot[recipe.RecipeIndex];

			bool retValue = true;

			ExecuteInCraftingGuiEnvironment(() => {
				if (!RecipeLoader.RecipeAvailable(recipe))
					retValue = false;
			});

			return retValue;
		}

		private static int IsAvailable_GetItemCount(Recipe recipe, int type, Dictionary<int, int> itemCountsDictionary) {
			ClampedArithmetic count = 0;
			bool useRecipeGroup = false;
			foreach (var (item, quantity) in itemCountsDictionary) {
				if (RecipeGroupMatch(recipe, item, type)) {
					count += quantity;
					useRecipeGroup = true;
				}
			}

			if (!useRecipeGroup && itemCountsDictionary.TryGetValue(type, out int amount))
				count += amount;

			return count;
		}

		public class PlayerZoneCache {
			public readonly bool[] origAdjTile;
			public readonly bool oldAdjWater;
			public readonly bool oldAdjLava;
			public readonly bool oldAdjHoney;
			public readonly bool oldAlchemyTable;
			public readonly bool oldSnow;
			public readonly bool oldGraveyard;

			private PlayerZoneCache() {
				Player player = Main.LocalPlayer;
				origAdjTile = player.adjTile.ToArray();
				oldAdjWater = player.adjWater;
				oldAdjLava = player.adjLava;
				oldAdjHoney = player.adjHoney;
				oldAlchemyTable = player.alchemyTable;
				oldSnow = player.ZoneSnow;
				oldGraveyard = player.ZoneGraveyard;
			}

			private static PlayerZoneCache cache;

			public static void Cache() {
				if (cache is not null)
					return;

				cache = new PlayerZoneCache();
			}

			public static void FreeCache(bool destroy) {
				if (cache is not PlayerZoneCache c)
					return;

				if (destroy)
					cache = null;

				Player player = Main.LocalPlayer;

				player.adjTile = c.origAdjTile;
				player.adjWater = c.oldAdjWater;
				player.adjLava = c.oldAdjLava;
				player.adjHoney = c.oldAdjHoney;
				player.alchemyTable = c.oldAlchemyTable;
				player.ZoneSnow = c.oldSnow;
				player.ZoneGraveyard = c.oldGraveyard;
			}
		}

		internal static void ExecuteInCraftingGuiEnvironment(Action action)
		{
			ArgumentNullException.ThrowIfNull(action);

			PlayerZoneCache.Cache();

			Player player = Main.LocalPlayer;

			try
			{
				player.adjTile = adjTiles;
				player.adjWater = adjWater;
				player.adjLava = adjLava;
				player.adjHoney = adjHoney;
				player.alchemyTable = alchemyTable;
				player.ZoneSnow = zoneSnow;
				player.ZoneGraveyard = graveyard;

				action();
			} finally {
				PlayerZoneCache.FreeCache(false);
			}
		}

		private static List<ItemInfo> storageItemInfo;

		internal static bool PassesBlock(Recipe recipe)
		{
			if (recipe is null || storageItemInfo is null)
				return false;

			NetHelper.Report(true, "Checking if recipe passes \"blocked ingredients\" check...");

			bool success;
			if (MagicStorageConfig.IsRecursionEnabled && recipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe)) {
				var simulation = new CraftingSimulation();
				simulation.SimulateCrafts(recursiveRecipe, craftAmountTarget, GetCurrentInventory(cloneIfBlockEmpty: true));

				success = PassesBlock_CheckSimulation(simulation);
			} else
				success = PassesBlock_CheckRecipe(recipe);

			NetHelper.Report(true, $"Recipe {(success ? "passed" : "failed")} the ingredients check");
			return success;
		}

		private static bool PassesBlock_CheckRecipe(Recipe recipe) {
			foreach (Item ingredient in recipe.requiredItem) {
				int stack = ingredient.stack;
				bool useRecipeGroup = false;

				foreach (ItemInfo item in storageItemInfo) {
					if (!blockStorageItems.Contains(new ItemData(item)) && RecipeGroupMatch(recipe, item.type, ingredient.type)) {
						stack -= item.stack;
						useRecipeGroup = true;

						if (stack <= 0)
							goto nextIngredient;
					}
				}

				if (!useRecipeGroup) {
					foreach (ItemInfo item in storageItemInfo) {
						if (!blockStorageItems.Contains(new ItemData(item)) && item.type == ingredient.type) {
							stack -= item.stack;

							if (stack <= 0)
								goto nextIngredient;
						}
					}
				}

				if (stack > 0)
					return false;

				nextIngredient: ;
			}

			return true;
		}

		private static bool PassesBlock_CheckSimulation(CraftingSimulation simulation) {
			foreach (RequiredMaterialInfo material in simulation.RequiredMaterials) {
				int stack = material.stack;

				foreach (int type in material.GetValidItems()) {
					foreach (ItemInfo item in storageItemInfo) {
						if (!blockStorageItems.Contains(new ItemData(item)) && item.type == type) {
							stack -= item.stack;

							if (stack <= 0)
								goto nextMaterial;
						}
					}
				}

				if (stack > 0)
					return false;

				nextMaterial: ;
			}

			return true;
		}

		private static void RefreshStorageItems(StorageGUI.ThreadContext thread = null)
		{
			NetHelper.Report(true, "Updating stored ingredients collection and result item...");

			storageItems.Clear();
			storageItemInfo = new();
			storageItemsFromModules.Clear();
			result = null;
			if (selectedRecipe is null) {
				thread?.InitAsCompleted("Populating stored ingredients");
				NetHelper.Report(true, "Failed.  No recipe is selected.");
				return;
			}

			int index = 0;
			bool hasItemFromStorage = false;
			if (!MagicStorageConfig.IsRecursionEnabled || !selectedRecipe.HasRecursiveRecipe() || GetCraftingSimulationForCurrentRecipe() is not CraftingSimulation { AmountCrafted: >0 } simulation) {
				NetHelper.Report(false, "Recursion was disabled or recipe did not have a recursive recipe");

				thread?.InitTaskSchedule(sourceItems.Count, "Populating stored ingredients");

				foreach (List<Item> itemsFromSource in sourceItems) {
					CheckStorageItemsForRecipe(selectedRecipe, itemsFromSource, null, checkResultItem: true, index, ref hasItemFromStorage);
					index++;

					thread?.CompleteOneTask();
				}
			} else {
				NetHelper.Report(false, "Recipe had a recursive recipe, processing recursion tree...");

				// Check each recipe in the tree
				IEnumerable<Recipe> recipes = simulation.UsedRecipes;

				// Evaluate now so the total task count can be used
				List<Recipe> usedRecipes = recipes.ToList();

				thread?.InitTaskSchedule(usedRecipes.Count * sourceItems.Count, "Populating stored ingredients");

				bool checkedHighestRecipe = false;
				List<bool[]> wasItemAdded = new List<bool[]>();
				foreach (Recipe recipe in usedRecipes) {
					index = 0;

					foreach (List<Item> itemsFromSource in sourceItems) {
						if (wasItemAdded.Count <= index)
							wasItemAdded.Add(new bool[itemsFromSource.Count]);

						// Only allow the "final recipe" (i.e. the first in the list) to affect the result item
						CheckStorageItemsForRecipe(recipe, itemsFromSource, wasItemAdded[index], checkResultItem: !checkedHighestRecipe, index, ref hasItemFromStorage);

						index++;

						thread?.CompleteOneTask();
					}

					checkedHighestRecipe = true;
				}
			}

			var resultItemList = CompactItemListWithModuleData(storageItems, storageItemsFromModules, out var moduleItemsList, thread);
			if (resultItemList.Count != storageItems.Count) {
				//Update the lists since items were compacted
				storageItems.Clear();
				storageItems.AddRange(resultItemList);
				storageItemInfo.Clear();
				storageItemInfo.AddRange(storageItems.Select(static i => new ItemInfo(i)));
				storageItemsFromModules.Clear();
				storageItemsFromModules.AddRange(moduleItemsList);
			}

			result ??= new Item(selectedRecipe.createItem.type, 0);

			NetHelper.Report(true, $"Success! Found {storageItems.Count} items and {(result.IsAir ? "no result items" : "a result item")}");
		}

		private static void CheckStorageItemsForRecipe(Recipe recipe, List<Item> itemsFromSource, bool[] wasItemAdded, bool checkResultItem, int index, ref bool hasItemFromStorage) {
			int addedIndex = 0;

			foreach (Item item in itemsFromSource) {
				if (item.type != selectedRecipe.createItem.type && wasItemAdded?[addedIndex] is not true) {
					foreach (Item reqItem in recipe.requiredItem) {
						if (item.type == reqItem.type || RecipeGroupMatch(recipe, item.type, reqItem.type)) {
							//Module items must refer to the original item instances
							Item clone = index >= numItemsWithoutSimulators ? item : item.Clone();
							storageItems.Add(clone);
							storageItemInfo.Add(new(clone));
							storageItemsFromModules.Add(index >= numItemsWithoutSimulators);

							if (wasItemAdded is not null)
								wasItemAdded[addedIndex] = true;
						}
					}
				}

				addedIndex++;

				if (checkResultItem && item.type == recipe.createItem.type) {
					Item source = itemsFromSource[0];

					if (index < numItemsWithoutSimulators) {
						result = source;
						hasItemFromStorage = true;
					} else if (!hasItemFromStorage)
						result = source;
				}
			}
		}

		public static bool RecipeGroupMatch(Recipe recipe, int inventoryType, int requiredType)
		{
			foreach (int num in recipe.acceptedGroups)
			{
				RecipeGroup recipeGroup = RecipeGroup.recipeGroups[num];
				if (recipeGroup.ContainsItem(inventoryType) && recipeGroup.ContainsItem(requiredType))
					return true;
			}

			return false;
		}

		internal static void SetSelectedRecipe(Recipe recipe)
		{
			ArgumentNullException.ThrowIfNull(recipe);

			NetHelper.Report(true, "Reassigning current recipe...");

			selectedRecipe = recipe;
			RefreshStorageItems();
			blockStorageItems.Clear();

			NetHelper.Report(true, "Successfully reassigned current recipe!");
		}

		internal static void SlotFocusLogic()
		{
			if (StorageGUI.CurrentlyRefreshing)
				return;  // Delay logic until threading stops

			if (result == null || result.IsAir || !Main.mouseItem.IsAir && (!ItemCombining.CanCombineItems(Main.mouseItem, result) || Main.mouseItem.stack >= Main.mouseItem.maxStack))
			{
				ResetSlotFocus();
			}
			else
			{
				if (rightClickTimer <= 0)
				{
					rightClickTimer = maxRightClickTimer;
					maxRightClickTimer = maxRightClickTimer * 3 / 4;
					if (maxRightClickTimer <= 0)
						maxRightClickTimer = 1;
					Item withdrawn = DoWithdrawResult(1);
					if (Main.mouseItem.IsAir)
						Main.mouseItem = withdrawn;
					else {
						Utility.CallOnStackHooks(Main.mouseItem, withdrawn, withdrawn.stack);

						Main.mouseItem.stack += withdrawn.stack;
					}

					SoundEngine.PlaySound(SoundID.MenuTick);
					
					StorageGUI.SetRefresh();
				}

				rightClickTimer--;
			}
		}

		internal static void ResetSlotFocus()
		{
			slotFocus = false;
			rightClickTimer = 0;
			maxRightClickTimer = StartMaxRightClickTimer;
		}

		private static List<Item> CompactItemList(List<Item> items) {
			List<Item> compacted = new();

			for (int i = 0; i < items.Count; i++) {
				Item item = items[i];

				if (item.IsAir)
					continue;

				bool fullyCompacted = false;
				for (int j = 0; j < compacted.Count; j++) {
					Item existing = compacted[j];

					if (ItemCombining.CanCombineItems(item, existing)) {
						if (existing.stack + item.stack <= existing.maxStack) {
							Utility.CallOnStackHooks(existing, item, item.stack);

							existing.stack += item.stack;
							item.stack = 0;
							fullyCompacted = true;
						} else {
							int diff = existing.maxStack - existing.stack;

							Utility.CallOnStackHooks(existing, item, diff);

							existing.stack = existing.maxStack;
							item.stack -= diff;
						}

						break;
					}
				}

				if (item.IsAir)
					continue;

				if (!fullyCompacted)
					compacted.Add(item);
			}

			return compacted;
		}

		private static List<Item> CompactItemListWithModuleData(List<Item> items, List<bool> moduleItems, out List<bool> moduleItemsResult, StorageGUI.ThreadContext thread = null) {
			List<Item> compacted = new();
			List<int> compactedSource = new();

			thread?.InitTaskSchedule(items.Count, "Aggregating stored ingredients (1/2)");

			for (int i = 0; i < items.Count; i++) {
				Item item = items[i];

				if (item.IsAir) {
					thread?.CompleteOneTask();
					continue;
				}

				bool fullyCompacted = false;
				for (int j = 0; j < compacted.Count; j++) {
					Item existing = compacted[j];

					if (ItemCombining.CanCombineItems(item, existing) && moduleItems[i] == moduleItems[compactedSource[j]] && !moduleItems[i]) {
						if (existing.stack + item.stack <= existing.maxStack) {
							existing.stack += item.stack;
							item.stack = 0;
							fullyCompacted = true;
						} else {
							int diff = existing.maxStack - existing.stack;

							Utility.CallOnStackHooks(existing, item, diff);

							existing.stack = existing.maxStack;
							item.stack -= diff;
						}

						break;
					}
				}

				if (item.IsAir) {
					thread?.CompleteOneTask();
					continue;
				}

				if (!fullyCompacted) {
					compacted.Add(item);
					compactedSource.Add(i);
				}

				thread?.CompleteOneTask();
			}

			thread?.InitTaskSchedule(1, "Aggregating stored ingredients (2/2)");

			moduleItemsResult = compactedSource.Select(m => moduleItems[m]).ToList();

			thread?.CompleteOneTask();

			return compacted;
		}

		/// <summary>
		/// Attempts to craft a certain amount of items from a Crafting Access
		/// </summary>
		/// <param name="craftingAccess">The tile entity for the Crafting Access to craft items from</param>
		/// <param name="toCraft">How many items should be crafted</param>
		public static void Craft(TECraftingAccess craftingAccess, int toCraft) {
			if (craftingAccess is null)
				return;

			StoragePlayer.StorageHeartAccessWrapper wrapper = new(craftingAccess);

			//OpenStorage() handles setting the CraftingGUI to use the new storage and Dispose()/CloseStorage() handles reverting it back
			if (wrapper.Valid) {
				using (wrapper.OpenStorage())
					Craft(toCraft);
			}
		}

		private class CraftingContext {
			public List<Item> sourceItems, availableItems, toWithdraw, results;

			public List<bool> fromModule;

			public EnvironmentSandbox sandbox;

			public List<Item> consumedItemsFromModules;

			public IEnumerable<EnvironmentModule> modules;

			public int toCraft;

			public bool simulation;

			public IEnumerable<Item> ConsumedItems => toWithdraw.Concat(consumedItemsFromModules);
		}

		/// <summary>
		/// Attempts to craft a certain amount of items from the currently assigned Crafting Access.
		/// </summary>
		/// <param name="toCraft">How many items should be crafted</param>
		public static void Craft(int toCraft) {
			TEStorageHeart heart = GetHeart();
			if (heart is null)
				return;  // Bail

			NetHelper.Report(true, $"Attempting to craft {toCraft} {Lang.GetItemNameValue(selectedRecipe.createItem.type)}");

			// Additional safeguard against absurdly high craft targets
			int origCraftRequest = toCraft;
			toCraft = Math.Min(toCraft, AmountCraftableForCurrentRecipe());

			if (toCraft != origCraftRequest)
				NetHelper.Report(false, $"Craft amount reduced to {toCraft}");

			if (toCraft <= 0) {
				NetHelper.Report(false, "Amount to craft was less than 1, aborting");
				return;
			}

			CraftingContext context;
			if (MagicStorageConfig.IsRecursionEnabled && selectedRecipe.HasRecursiveRecipe()) {
				// Recursive crafting uses special logic which can't just be injected into the previous logic
				context = Craft_WithRecursion(toCraft);

				if (context is null)
					return;  // Bail
			} else {
				context = InitCraftingContext(toCraft);

				int target = toCraft;

				ExecuteInCraftingGuiEnvironment(() => Craft_DoStandardCraft(context));

				NetHelper.Report(true, $"Crafted {target - context.toCraft} items");

				if (target == context.toCraft) {
					//Could not craft anything, bail
					return;
				}
			}

			NetHelper.Report(true, "Compacting results list...");

			context.toWithdraw = CompactItemList(context.toWithdraw);
			
			context.results = CompactItemList(context.results);

			if (Main.netMode == NetmodeID.SinglePlayer) {
				NetHelper.Report(true, "Spawning excess results on player...");

				foreach (Item item in HandleCraftWithdrawAndDeposit(heart, context.toWithdraw, context.results))
					Main.LocalPlayer.QuickSpawnClonedItem(new EntitySource_TileEntity(heart), item, item.stack);

				StorageGUI.SetRefresh();
			} else if (Main.netMode == NetmodeID.MultiplayerClient) {
				NetHelper.Report(true, "Sending craft results to server...");

				NetHelper.SendCraftRequest(heart.Position, context.toWithdraw, context.results);
			}
		}

		private static void Craft_DoStandardCraft(CraftingContext context) {
			//Do lazy crafting first (batch loads of ingredients into one "craft"), then do normal crafting
			if (!AttemptLazyBatchCraft(context)) {
				NetHelper.Report(false, "Batch craft operation failed.  Attempting repeated crafting of a single result.");

				AttemptCraft(AttemptSingleCraft, context);
			}
		}

		private static CraftingContext InitCraftingContext(int toCraft) {
			var sourceItems = storageItems.Where(item => !blockStorageItems.Contains(new ItemData(item))).ToList();
			var availableItems = sourceItems.Select(item => item.Clone()).ToList();
			var fromModule = storageItemsFromModules.Where((_, n) => !blockStorageItems.Contains(new ItemData(storageItems[n]))).ToList();
			List<Item> toWithdraw = new(), results = new();

			TEStorageHeart heart = GetHeart();

			EnvironmentSandbox sandbox = new(Main.LocalPlayer, heart);

			return new CraftingContext() {
				sourceItems = sourceItems,
				availableItems = availableItems,
				toWithdraw = toWithdraw,
				results = results,
				sandbox = sandbox,
				consumedItemsFromModules = new(),
				fromModule = fromModule,
				modules = heart?.GetModules() ?? Array.Empty<EnvironmentModule>(),
				toCraft = toCraft
			};
		}

		private static Dictionary<int, int> GetItemCountsWithBlockedItemsRemoved(bool cloneIfBlockEmpty = false) {
			if (!cloneIfBlockEmpty && blockStorageItems.Count == 0)
				return itemCounts;

			Dictionary<int, int> counts = new(itemCounts);

			foreach (var data in blockStorageItems)
				counts.Remove(data.Type);

			return counts;
		}

		public static AvailableRecipeObjects GetCurrentInventory(bool cloneIfBlockEmpty = false) {
			bool[] availableRecipes = currentlyThreading && StorageGUI.activeThread.state is ThreadState { recipeConditionsMetSnapshot: bool[] snapshot } ? snapshot : null;
			return new AvailableRecipeObjects(adjTiles, GetItemCountsWithBlockedItemsRemoved(cloneIfBlockEmpty), availableRecipes);
		}

		private static Recipe recentRecipeSimulation;
		private static CraftingSimulation simulatedCraftForCurrentRecipe;

		public static CraftingSimulation GetCraftingSimulationForCurrentRecipe() {
			if (object.ReferenceEquals(recentRecipeSimulation, selectedRecipe) && simulatedCraftForCurrentRecipe is not null)
				return simulatedCraftForCurrentRecipe;

			if (!selectedRecipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe))
				return new CraftingSimulation();

			// Calculate the value
			recentRecipeSimulation = selectedRecipe;
			CraftingSimulation simulation = new CraftingSimulation();
			simulation.SimulateCrafts(recursiveRecipe, craftAmountTarget, GetCurrentInventory(cloneIfBlockEmpty: true));
			simulatedCraftForCurrentRecipe = simulation;
			return simulation;
		}

		public static void ResetCachedCraftingSimulation() {
			recentRecipeSimulation = null;
			simulatedCraftForCurrentRecipe = null;
		}

		private static CraftingContext Craft_WithRecursion(int toCraft) {
			// Unlike normal crafting, the crafting tree has to be respected
			// This means that simple IsAvailable and AmountCraftable checks would just slow it down
			// Hence, the logic here will just assume that it's craftable and just ignore branches in the recursion tree that aren't available or are already satisfied
			if (!selectedRecipe.TryGetRecursiveRecipe(out RecursiveRecipe recursiveRecipe))
				throw new InvalidOperationException("Recipe object did not have a RecursiveRecipe object assigned to it");

			if (toCraft <= 0)
				return null;  // Bail

			CraftingContext context = InitCraftingContext(toCraft);

			NetHelper.Report(true, "Attempting recurrent crafting...");

			// Local capturing
			var ctx = context;
			ExecuteInCraftingGuiEnvironment(() => Craft_DoRecursionCraft(ctx));

			// Sanity check
			selectedRecipe = recursiveRecipe.original;

			return context;
		}

		private static void Craft_DoRecursionCraft(CraftingContext ctx) {
			var simulation = GetCraftingSimulationForCurrentRecipe();

			if (simulation.AmountCrafted <= 0) {
				NetHelper.Report(false, "Crafting simulation resulted in zero crafts, aborting");
				return;
			}

			// At this point, the amount to craft has already been clamped by the max amount possible
			// Hence, just consume the items
			List<Item> consumedItems = new();

			ctx.simulation = true;

			foreach (var m in simulation.RequiredMaterials) {
				if (m.stack <= 0)
					continue;  // Safeguard: material was already "used up" by higher up recipes

				var material = m;

				List<Item> origWithdraw = new(ctx.toWithdraw);
				List<Item> origResults = new(ctx.results);
				List<Item> origFromModule = new(ctx.consumedItemsFromModules);

				bool notEnoughItems = true;

				foreach (int type in material.GetValidItems()) {
					Item item = new Item(type, material.stack);

					if (!CanConsumeItem(ctx, item, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed, checkRecipeGroup: false)) {
						if (wasAvailable) {
							NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(item.type)}\"");
							notEnoughItems = false;
							break;
						}
					} else {
						// Consume the item
						material = material.UpdateStack(-stackConsumed);
						item.stack = stackConsumed;
						consumedItems.Add(item);

						notEnoughItems = false;

						if (material.stack <= 0)
							break;
					}
				}

				if (notEnoughItems) {
					NetHelper.Report(false, $"Material requirement \"{Lang.GetItemNameValue(material.GetValidItems().First())}\" could not be met, aborting");
					return;
				}
			}

			// Immediately add the excess to the context's results
			ctx.results.AddRange(simulation.ExcessResults.Where(static i => i.stack > 0).Select(static i => new Item(i.type, i.stack, i.prefix)));

			ctx.simulation = false;

			NetHelper.Report(true, $"Recursion crafting used the following materials:\n  {
				(consumedItems.Count > 0
					? string.Join("\n  ", consumedItems.Select(static i => $"{i.stack} {Lang.GetItemNameValue(i.type)}"))
					: "none")
				}");

			// Actually consume the items
			foreach (Item item in consumedItems) {
				int stack = item.stack;
				AttemptToConsumeItem(ctx, item.type, ref stack, checkRecipeGroup: false);
			}

			NetHelper.Report(true, $"Success! Crafted {simulation.AmountCrafted} items and {simulation.ExcessResults.Count - 1} extra item types");
		}

		private static void AttemptCraft(Func<CraftingContext, bool> func, CraftingContext context) {
			// NOTE: [ThreadStatic] only runs the field initializer on one thread
			DroppedItems ??= new();

			while (context.toCraft > 0) {
				if (!func(context))
					break;  // Could not craft any more items

				Item resultItem = selectedRecipe.createItem.Clone();
				context.toCraft -= resultItem.stack;

				resultItem.Prefix(-1);
				context.results.Add(resultItem);

				CatchDroppedItems = true;
				DroppedItems.Clear();

				var consumed = context.ConsumedItems.ToList();

				RecipeLoader.OnCraft(resultItem, selectedRecipe, consumed);

				foreach (EnvironmentModule module in context.modules)
					module.OnConsumeItemsForRecipe(context.sandbox, selectedRecipe, consumed);

				CatchDroppedItems = false;

				context.results.AddRange(DroppedItems);
			}
		}

		private static bool AttemptLazyBatchCraft(CraftingContext context) {
			NetHelper.Report(false, "Attempting batch craft operation...");

			List<Item> origResults = new(context.results);
			List<Item> origWithdraw = new(context.toWithdraw);
			List<Item> origFromModule = new(context.consumedItemsFromModules);

			//Try to batch as many "crafts" into one craft as possible
			int crafts = (int)Math.Ceiling(context.toCraft / (float)selectedRecipe.createItem.stack);

			//Skip item consumption code for recipes that have no ingredients
			if (selectedRecipe.requiredItem.Count == 0) {
				NetHelper.Report(false, "Recipe had no ingredients, skipping consumption...");
				goto SkipItemConsumption;
			}

			context.simulation = true;

			List<Item> batch = new(selectedRecipe.requiredItem.Count);

			//Reduce the number of batch crafts until this recipe can be completely batched for the number of crafts
			while (crafts > 0) {
				bool didAttemptToConsumeItem = false;

				foreach (Item reqItem in selectedRecipe.requiredItem) {
					Item clone = reqItem.Clone();
					clone.stack *= crafts;

					if (!CanConsumeItem(context, clone, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed)) {
						if (wasAvailable) {
							NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(reqItem.type)}\". (Batching {crafts} crafts)");

							// Indicate to later logic that an attempt was made
							didAttemptToConsumeItem = true;
						} else {
							// Did not have enough items
							crafts--;
							batch.Clear();
							didAttemptToConsumeItem = false;
							break;
						}
					} else {
						//Consume the item
						clone.stack = stackConsumed;
						batch.Add(clone);
					}
				}

				if (batch.Count > 0 || didAttemptToConsumeItem) {
					//Successfully batched items for the craft
					break;
				}
			}

			// Remove any empty items since they wouldn't do anything anyway
			batch.RemoveAll(i => i.stack <= 0);

			context.simulation = false;

			if (crafts <= 0) {
				//Craft batching failed
				return false;
			}

			//Consume the batched items
			foreach (Item item in batch) {
				int stack = item.stack;

				AttemptToConsumeItem(context, item.type, ref stack);
			}

			NetHelper.Report(true, $"Batch crafting used the following materials:\n  {string.Join("\n  ", batch.Select(static i => $"{i.stack} {Lang.GetItemNameValue(i.type)}"))}");

			SkipItemConsumption:

			// NOTE: [ThreadStatic] only runs the field initializer on one thread
			DroppedItems ??= new();

			//Create the resulting items
			for (int i = 0; i < crafts; i++) {
				Item resultItem = selectedRecipe.createItem.Clone();
				context.toCraft -= resultItem.stack;

				resultItem.Prefix(-1);
				context.results.Add(resultItem);

				CatchDroppedItems = true;
				DroppedItems.Clear();

				var consumed = context.ConsumedItems.ToList();

				RecipeLoader.OnCraft(resultItem, selectedRecipe, consumed);

				foreach (EnvironmentModule module in context.modules)
					module.OnConsumeItemsForRecipe(context.sandbox, selectedRecipe, consumed);

				CatchDroppedItems = false;

				context.results.AddRange(DroppedItems);
			}

			NetHelper.Report(false, $"Batch craft operation succeeded ({crafts} crafts batched)");

			return true;
		}

		private static bool AttemptSingleCraft(CraftingContext context) {
			List<Item> origResults = new(context.results);
			List<Item> origWithdraw = new(context.toWithdraw);
			List<Item> origFromModule = new(context.consumedItemsFromModules);

			NetHelper.Report(false, "Attempting one craft operation...");

			context.simulation = true;

			List<int> stacksConsumed = new();

			foreach (Item reqItem in selectedRecipe.requiredItem) {
				if (!CanConsumeItem(context, reqItem, origWithdraw, origResults, origFromModule, out bool wasAvailable, out int stackConsumed)) {
					if (wasAvailable)
						NetHelper.Report(false, $"Skipping consumption of item \"{Lang.GetItemNameValue(reqItem.type)}\".");
					else {
						NetHelper.Report(false, $"Required item \"{Lang.GetItemNameValue(reqItem.type)}\" was not available.");
						return false;  // Did not have enough items
					}
				} else
					NetHelper.Report(false, $"Required item \"{Lang.GetItemNameValue(reqItem.type)}\" was available.");

				stacksConsumed.Add(stackConsumed);
			}

			context.simulation = false;

			//Consume the source items as well since the craft was successful
			int consumeStackIndex = 0;
			foreach (Item reqItem in selectedRecipe.requiredItem) {
				int stack = stacksConsumed[consumeStackIndex];
				AttemptToConsumeItem(context, reqItem.type, ref stack);
				consumeStackIndex++;
			}

			NetHelper.Report(false, "Craft operation succeeded");

			return true;
		}

		private static bool CanConsumeItem(CraftingContext context, Item reqItem, List<Item> origWithdraw, List<Item> origResults, List<Item> origFromModule, out bool wasAvailable, out int stackConsumed, bool checkRecipeGroup = true) {
			wasAvailable = true;

			stackConsumed = reqItem.stack;

			RecipeLoader.ConsumeItem(selectedRecipe, reqItem.type, ref stackConsumed);

			foreach (EnvironmentModule module in context.modules)
				module.ConsumeItemForRecipe(context.sandbox, selectedRecipe, reqItem.type, ref stackConsumed);

			if (stackConsumed <= 0)
				return false;

			int stack = stackConsumed;
			bool consumeSucceeded = AttemptToConsumeItem(context, reqItem.type, ref stack, checkRecipeGroup);

			if (stack > 0 || !consumeSucceeded) {
				context.results.Clear();
				context.results.AddRange(origResults);

				context.toWithdraw.Clear();
				context.toWithdraw.AddRange(origWithdraw);

				context.consumedItemsFromModules.Clear();
				context.consumedItemsFromModules.AddRange(origFromModule);

				wasAvailable = false;
				return false;
			}

			return true;
		}

		private static bool AttemptToConsumeItem(CraftingContext context, int reqType, ref int stack, bool checkRecipeGroup = true) {
			return CheckContextItemCollection(context, context.results, reqType, ref stack, null, checkRecipeGroup)
				|| CheckContextItemCollection(context, GetAvailableItems(context), reqType, ref stack, OnAvailableItemConsumed, checkRecipeGroup)
				|| CheckContextItemCollection(context, GetModuleItems(context), reqType, ref stack, OnModuleItemConsumed, checkRecipeGroup);
		}

		private static IEnumerable<Item> GetAvailableItems(CraftingContext context) {
			for (int i = 0; i < context.sourceItems.Count; i++) {
				if (!context.fromModule[i])
					yield return context.availableItems[i];
			}
		}

		private static void OnAvailableItemConsumed(CraftingContext context, int index, Item tryItem, int stackToConsume) {
			if (!context.simulation) {
				Item consumed = tryItem.Clone();
				consumed.stack = stackToConsume;

				context.toWithdraw.Add(consumed);
			}
		}

		private static IEnumerable<Item> GetModuleItems(CraftingContext context) {
			for (int i = 0; i < context.sourceItems.Count; i++) {
				if (context.fromModule[i])
					yield return context.sourceItems[i];
			}
		}

		private static void OnModuleItemConsumed(CraftingContext context, int index, Item tryItem, int stackToConsume) {
			if (!context.simulation) {
				Item consumed = tryItem.Clone();
				consumed.stack = stackToConsume;

				context.consumedItemsFromModules.Add(consumed);
			}
		}

		private static bool CheckContextItemCollection(CraftingContext context, IEnumerable<Item> items, int reqType, ref int stack, Action<CraftingContext, int, Item, int> onItemConsumed, bool checkRecipeGroup = true) {
			int index = 0;
			foreach (Item tryItem in !context.simulation ? items : items.Select(static i => new Item(i.type, i.stack))) {
				// Recursion crafting can cause the item stack to be zero
				if (tryItem.stack <= 0)
					continue;

				if (reqType == tryItem.type || (checkRecipeGroup && RecipeGroupMatch(selectedRecipe, tryItem.type, reqType))) {
					int stackToConsume;

					if (tryItem.stack > stack) {
						stackToConsume = stack;
						stack = 0;
					} else {
						stackToConsume = tryItem.stack;
						stack -= tryItem.stack;
					}

					if (!context.simulation)
						OnConsumeItemForRecipe_Obsolete(context, tryItem, stackToConsume);

					onItemConsumed?.Invoke(context, index, tryItem, stackToConsume);

					tryItem.stack -= stackToConsume;

					if (tryItem.stack <= 0)
						tryItem.type = ItemID.None;

					if (stack <= 0)
						break;
				}

				index++;
			}

			return stack <= 0;
		}

		[Obsolete]
		private static void OnConsumeItemForRecipe_Obsolete(CraftingContext context, Item tryItem, int stackToConsume) {
			foreach (var module in context.modules)
				module.OnConsumeItemForRecipe(context.sandbox, tryItem, stackToConsume);
		}

		internal static List<Item> HandleCraftWithdrawAndDeposit(TEStorageHeart heart, List<Item> toWithdraw, List<Item> results)
		{
			var items = new List<Item>();
			foreach (Item tryWithdraw in toWithdraw)
			{
				Item withdrawn = heart.TryWithdraw(tryWithdraw, false);
				if (!withdrawn.IsAir)
					items.Add(withdrawn);
				if (withdrawn.stack < tryWithdraw.stack)
				{
					for (int k = 0; k < items.Count; k++)
					{
						heart.DepositItem(items[k]);
						if (items[k].IsAir)
						{
							items.RemoveAt(k);
							k--;
						}
					}

					return items;
				}
			}

			items.Clear();
			foreach (Item result in results)
			{
				heart.DepositItem(result);
				if (!result.IsAir)
					items.Add(result);
			}

			return items;
		}

		internal static bool TryDepositResult(Item item)
		{
			int oldStack = item.stack;
			int oldType = item.type;
			TEStorageHeart heart = GetHeart();

			if (heart is null)
				return false;

			heart.TryDeposit(item);

			if (oldStack != item.stack) {
				SetNextDefaultRecipeCollectionToRefresh(oldType);

				return true;
			}

			return false;
		}

		internal static Item DoWithdrawResult(int amountToWithdraw, bool toInventory = false)
		{
			TEStorageHeart heart = GetHeart();
			if (heart is null)
				return new Item();

			Item clone = result.Clone();
			clone.stack = Math.Min(amountToWithdraw, clone.maxStack);

			if (Main.netMode == NetmodeID.MultiplayerClient) {
				ModPacket packet = heart.PrepareClientRequest(toInventory ? TEStorageHeart.Operation.WithdrawToInventoryThenTryModuleInventory : TEStorageHeart.Operation.WithdrawThenTryModuleInventory);
				ItemIO.Send(clone, packet, true, true);
				packet.Send();
				return new Item();
			}

			Item withdrawn = heart.Withdraw(clone, false);

			if (withdrawn.IsAir)
				withdrawn = TryToWithdrawFromModuleItems(amountToWithdraw);

			return withdrawn;
		}

		internal static Item TryToWithdrawFromModuleItems(int amountToWithdraw) {
			Item withdrawn;
			if (items.Count != numItemsWithoutSimulators) {
				//Heart did not contain the item; try to withdraw from the module items
				Item item = result.Clone();
				item.stack = Math.Min(amountToWithdraw, item.maxStack);

				TEStorageUnit.WithdrawFromItemCollection(sourceItemsFromModules, item, out withdrawn,
					onItemRemoved: k => {
						int index = k + numItemsWithoutSimulators;
						
						items.RemoveAt(index);
					},
					onItemStackReduced: (k, stack) => {
						int index = k + numItemsWithoutSimulators;

						Item item = items[index];
						itemCounts[item.type] -= stack;
					});

				if (!withdrawn.IsAir) {
					StorageGUI.SetRefresh();
					SetNextDefaultRecipeCollectionToRefresh(withdrawn.type);
				}
			} else
				withdrawn = new Item();

			return withdrawn;
		}
	}
}
