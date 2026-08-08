#region Copyright & License Information
/*
 * Copyright (c) The OpenRA Developers and Contributors
 * This file is part of OpenRA, which is free software. It is made
 * available to you under the terms of the GNU General Public License
 * as published by the Free Software Foundation, either version 3 of
 * the License, or (at your option) any later version. For more
 * information, see COPYING.
 */
#endregion

namespace OpenRA.Mods.CN.Traits.BotModules.Squads
{
	sealed class CNStateMachine
	{
		ICNState currentState;

		public void Update(CNSquad squad)
		{
			currentState?.Tick(squad);
		}

		public void ChangeState(CNSquad squad, ICNState newState)
		{
			currentState?.Deactivate(squad);

			if (newState != null)
				currentState = newState;

			currentState?.Activate(squad);
		}

		public bool IsInState<T>() where T : ICNState => currentState is T;

		public bool IsInAnyState<T1, T2>() where T1 : ICNState where T2 : ICNState =>
			currentState is T1 || currentState is T2;

		public bool IsInTimeCriticalState => currentState is ICNTimeCriticalState;
	}

	interface ICNState
	{
		void Activate(CNSquad squad);
		void Tick(CNSquad squad);
		void Deactivate(CNSquad squad);
	}

	/// <summary>
	/// Marks a state whose decisions are time-critical even though the squad holds no combat target:
	/// arrival checks and unload sequencing, where a late decision is visible as units standing
	/// around. The squad manager updates these on the engaged cadence rather than the idle one.
	/// </summary>
	interface ICNTimeCriticalState : ICNState { }
}
