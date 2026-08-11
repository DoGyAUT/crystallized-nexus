#region Copyright & License Information
/*
 * Copyright (c) The Crystallized Nexus Developers
 * This file is part of the Crystallized Nexus OpenRA mod.
 */
#endregion

using System.Collections.Generic;
using System.Linq;
using OpenRA.Graphics;
using OpenRA.Mods.CN.Traits;
using OpenRA.Mods.Common;
using OpenRA.Mods.Common.Orders;
using OpenRA.Mods.Common.Traits;
using OpenRA.Orders;
using OpenRA.Primitives;
using OpenRA.Traits;
using OpenRA.Widgets;

namespace OpenRA.Mods.CN.Orders
{
	/// <summary>
	/// Footprint-targeting order generator for ordering Builder units to construct a structure.
	/// Renders the same placement preview as normal building placement; on a valid click it issues
	/// a "BuildStructure" order to each selected builder. Implemented as a standalone
	/// <see cref="IOrderGenerator"/> (like PlaceBuildingOrderGenerator) so the placement action
	/// button confirms instead of being swallowed by unit selection.
	/// </summary>
	public class BuildStructureOrderGenerator : IOrderGenerator
	{
		readonly string worldDefaultCursor = ChromeMetrics.Get<string>("WorldDefaultCursor");

		readonly string buildingName;
		readonly ActorInfo actorInfo;
		readonly BuildingInfo buildingInfo;
		readonly IPlaceBuildingPreview preview;
		readonly PlaceBuildingInfo placeBuildingInfo;
		readonly Viewport viewport;
		readonly GameSettings gameSettings;
		Actor[] subjects;

		public BuildStructureOrderGenerator(World world, IEnumerable<Actor> subjects, string buildingName, WorldRenderer worldRenderer)
		{
			this.buildingName = buildingName;
			this.subjects = subjects.Where(a => !a.IsDead && a.IsInWorld).ToArray();

			var owner = this.subjects[0].Owner;
			placeBuildingInfo = owner.PlayerActor.Info.TraitInfo<PlaceBuildingInfo>();
			viewport = worldRenderer.Viewport;
			gameSettings = Game.Settings.Game;

			if (gameSettings.MouseControlStyle == MouseControlStyle.Classic)
				world.Selection.Clear();

			actorInfo = world.Map.Rules.Actors[buildingName];
			buildingInfo = actorInfo.TraitInfo<BuildingInfo>();

			var previewGeneratorInfo = actorInfo.TraitInfoOrDefault<IPlaceBuildingPreviewGeneratorInfo>();
			if (previewGeneratorInfo != null)
			{
				var faction = actorInfo.TraitInfoOrDefault<BuildableInfo>()?.ForceFaction ?? owner.Faction.InternalName;

				var td = new TypeDictionary
				{
					new FactionInit(faction),
					new OwnerInit(owner),
				};

				foreach (var api in actorInfo.TraitInfos<IActorPreviewInitInfo>())
					foreach (var o in api.ActorPreviewInits(actorInfo, ActorPreviewType.PlaceBuilding))
						td.Add(o);

				preview = previewGeneratorInfo.CreatePreview(worldRenderer, actorInfo, td);
			}
		}

		public MouseButton ActionButton => gameSettings.ResolveActionButton(MouseActionType.PlaceBuilding);

		static PlaceBuildingCellType MakeCellType(bool valid) =>
			valid ? PlaceBuildingCellType.Valid : PlaceBuildingCellType.Invalid;

		CPos TopLeft
		{
			get
			{
				var offsetPos = Viewport.LastMousePos;
				if (preview != null)
					offsetPos = viewport.WorldToViewPx(viewport.ViewToWorldPx(offsetPos) + preview.TopLeftScreenOffset);

				return viewport.ViewToWorld(offsetPos);
			}
		}

		public IEnumerable<Order> Order(World world, CPos cell, int2 worldPixel, MouseInput mi)
		{
			var actionButton = gameSettings.ResolveActionButton(MouseActionType.PlaceBuilding);
			var cancelButton = gameSettings.ResolveCancelButton(MouseActionType.PlaceBuilding);

			if ((mi.Button == actionButton && mi.Event == MouseInputEvent.Down) ||
				(mi.Button == cancelButton && mi.Event == MouseInputEvent.Up))
			{
				if (mi.Button == cancelButton)
				{
					world.CancelInputMode();
					return [];
				}

				var ret = InnerOrder(world).ToArray();
				if (ret.Length > 0)
					world.CancelInputMode();

				return ret;
			}

			return [];
		}

		IEnumerable<Order> InnerOrder(World world)
		{
			if (world.Paused)
				yield break;

			subjects = subjects.Where(a => !a.IsDead && a.IsInWorld).ToArray();
			if (subjects.Length == 0)
			{
				world.CancelInputMode();
				yield break;
			}

			var owner = subjects[0].Owner;
			var topLeft = TopLeft;

			if (!world.CanPlaceBuilding(topLeft, actorInfo, buildingInfo, null) ||
				!buildingInfo.IsCloseEnoughToBase(world, owner, actorInfo, topLeft))
			{
				foreach (var order in ClearBlockersOrders(topLeft))
					yield return order;

				Game.Sound.PlayNotification(world.Map.Rules, owner, "Speech", placeBuildingInfo.CannotPlaceNotification, owner.Faction.InternalName);
				TextNotificationsManager.AddTransientLine(owner, placeBuildingInfo.CannotPlaceTextNotification);

				yield break;
			}

			foreach (var builder in subjects)
				yield return new Order("BuildStructure", builder, Target.FromCell(world, topLeft), false)
				{
					TargetString = buildingName,
					SuppressVisualFeedback = true,
				};
		}

		void IOrderGenerator.Tick(World world) => preview?.Tick();

		IEnumerable<IRenderable> IOrderGenerator.Render(WorldRenderer wr, World world) => [];

		IEnumerable<IRenderable> IOrderGenerator.RenderAboveShroud(WorldRenderer wr, World world)
		{
			var topLeft = TopLeft;
			var footprint = new Dictionary<CPos, PlaceBuildingCellType>();
			var isCloseEnough = buildingInfo.IsCloseEnoughToBase(world, world.LocalPlayer, actorInfo, topLeft);
			foreach (var t in buildingInfo.Tiles(topLeft))
				footprint.Add(t, MakeCellType(isCloseEnough && world.IsCellBuildable(t, actorInfo, buildingInfo)));

			return preview?.Render(wr, topLeft, footprint) ?? [];
		}

		IEnumerable<IRenderable> IOrderGenerator.RenderAnnotations(WorldRenderer wr, World world)
			=> preview?.RenderAnnotations(wr, TopLeft) ?? [];

		public string GetCursor(World world, CPos cell, int2 worldPixel, MouseInput mi)
			=> worldDefaultCursor;

		bool IOrderGenerator.HandleKeyPress(KeyInput e) => false;

		void IOrderGenerator.Deactivate() { }

		void IOrderGenerator.SelectionChanged(World world, IEnumerable<Actor> selected)
		{
			subjects = selected.Where(a => !a.IsDead && a.IsInWorld && a.Info.HasTraitInfo<BuilderInfo>()).ToArray();
			if (subjects.Length == 0)
				world.CancelInputMode();
		}

		IEnumerable<Order> ClearBlockersOrders(CPos topLeft)
			=> AIUtils.ClearBlockersOrders(buildingInfo.Tiles(topLeft).ToList(), subjects[0].Owner);
	}
}
