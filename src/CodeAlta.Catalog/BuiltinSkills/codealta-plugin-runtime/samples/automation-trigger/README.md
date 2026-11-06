# Automation trigger sample

Adds the trigger **Branch moved** (`branch-watch/branch`) to automations: a polled source that reads the commit of a branch of the project and gives one event each time it moves, with the commit as the cursor CodeAlta keeps. The trigger comes with the script `branch-watch/log`, which the editor offers to run without a model: the script calls the plugin's own `alta branch-log add`, so nothing is sent to an agent.
