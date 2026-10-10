# Landing card sample

A card pinned on the landing page of CodeAlta Desktop (`GetLandingCards()`): the few things to do today. The plugin keeps the list (with `Services.State`); the card is an HTML fragment that shows the first three, a status text beside its title (`2 left`), and three actions: two run a command of the plugin (`PluginLandingCardAction.RunCommand`), one opens its canvas (`PluginLandingCardAction.OpenCanvas`). After a change the plugin calls `Services.Ui.InvalidateLandingCards()`, so the page asks for the card again.

Open the landing page with `/landing` (or `alta landing open`) to see the card. A card that returns `null` is left out; one that throws is shown as a card that could not be loaded, and the rest of the page is not affected.
