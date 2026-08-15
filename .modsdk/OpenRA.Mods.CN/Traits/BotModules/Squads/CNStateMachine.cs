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
			if (currentState == null)
				return;

			// Charged to the state rather than to the squad loop as a whole. SquadUpdates is the phase
			// with the worst single-call spikes left (605 ms in a heavy match), and the states differ
			// enormously in what they do - some only check arrival, others search the map for a target.
			// Type.Name is cached by the runtime, so this costs a timestamp pair per squad update.
			using (CNBotPerf.Sample(squad.Bot, currentState.GetType().Name))
				currentState.Tick(squad);
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
