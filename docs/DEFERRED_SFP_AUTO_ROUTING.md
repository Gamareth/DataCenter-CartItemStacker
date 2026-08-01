# Deferred SFP Auto-Routing Experiment

Cart Item Stacker intentionally leaves loose SFP modules to Data Center's
native interaction system. The player must click the specific compatible tray
that should receive the held module. The mod only observes the content change
and rearranges the complete tray within the trolley layout.

## Deferred idea

A future optional feature could let a loose SFP module be placed by clicking
any stored item on the trolley. The intended destination order was:

1. The fullest compatible partially filled tray.
2. An eligible compatible empty tray when no partial tray can accept it.
3. Earlier trolley slot as a deterministic tie-breaker.

## Lessons from the experiment

- A complete `SFPBox` contains `SFPModule` child components. Classification
  must identify the complete tray before searching for loose module components.
- Calling `SFPBox.ReturnSFPDirectly` alone moves the module but does not clear
  every native player-held-object reference. This allowed duplicate insertion
  and could block subsequent pickup.
- Remotely calling the complete `SFPBox.InteractOnClick` flow also executes the
  tray pickup branch, which can put the destination tray into the player's hand.
- Any future implementation must use one native-equivalent transaction that
  atomically inserts the module, clears the player's held-object state, leaves
  the destination tray stored, and updates tray occupancy.
- Never retry another destination after a partially successful insertion.

## Required acceptance tests before reconsideration

- Filled and empty trays remain distinct from loose modules.
- One click inserts exactly one module.
- The destination fill increases by exactly one.
- The module leaves the player's hand and cannot be inserted twice.
- The destination tray remains stored and selectable.
- Unrelated items can be picked up immediately after insertion.
- Failed insertion leaves both the held module and every tray unchanged.
- Save/load preserves the resulting tray contents and trolley layout.
