📦 Warehouse Manager
Smart Inventory Automation for TCG Card Shop Simulator

Version: v0.9

Warehouse Manager is a fully automated, stability-first inventory management mod built to work with the game’s systems — not against them.

It continuously evaluates your shop shelves and warehouse storage, calculates exactly what you're missing, converts that deficit into properly sized BIG box orders, and places them safely — without exceeding warehouse capacity.

The result

✔ Fully stocked shelves

✔ No warehouse overflow

✔ No wasted boxes

✔ No floor clutter

✔ Zero micromanagement

🧠 How It Works

Warehouse Manager performs real inventory math — no guessing, no brute force.

It:

Reads shop shelves in units

Reads warehouse shelves in box slots

Accounts for items across multiple shelves

Respects warehouse slot limits

Includes loose box units in calculations

Never over-orders

Formula Used
neededUnits = totalCapacity - currentUnits
boxesToOrder = floor(neededUnits / unitsPerBox)


Only boxes that fully fit within warehouse slot limits are ordered.

No overfill. No guesswork.

⚙️ Automation Modes

Warehouse Manager can run in one of the following modes:

🔹 Manual Mode

Run instantly using a configurable hotkey.

🔹 Every X Real-Time Minutes

Runs continuously at configurable real-world intervals.
(Does not stop at end of day.)

🔹 Once Per In-Game Day

Runs automatically at a specific in-game time you choose.

⚠ Important: Only enable one automation mode at a time.

📚 Automatic Catalog System (v0.9+)

Warehouse Manager now builds and maintains its item catalog automatically.

It detects buyable items and extracts:

Internal item key

Restock index

Units per BIG box

All data is merged into a persistent catalog file:

BepInEx/patchers/Warehouse Manager/WarehouseManager.catalog.json

What this means

No manual setup

No scanning hotkeys

No user interaction required

New prefab/modded items are learned automatically when detected

🧩 Prefab / APL Compatibility

Fully compatible with:

Enhanced Prefab Loader

Advanced Prefab Loader

Buyable prefab-based content

Warehouse Manager uses reflection and runtime discovery to remain compatible with modded content.

✅ Safe to install mid-save

✅ No dependency on custom shop UIs

🔗 Optional F(ate) Integration

If the F(ate) mod is installed and enabled:

Warehouse Manager automatically triggers box transfer from the floor to warehouse shelves after ordering

No extra configuration required

It just works™.

🛠 Debug Tools (Optional)

For advanced users and testing:

Inventory math logging

Warehouse compartment analysis

Detailed order planning logs

Dry-run mode (builds a plan without placing orders)

All debug features are hidden behind a single toggle to keep normal gameplay clean.

🛡 Stability First

Warehouse Manager is designed with safety as the top priority:

Does not modify base game files

Uses reflection-based access

Built around the game’s real systems

Avoids overfilling, race conditions, and unsafe calls

Tested under real gameplay conditions

This mod prioritizes correctness over speed.

📦 Requirements

BepInEx

TCG Card Shop Simulator

(Optional) F(ate) mod for automatic box movement

🚧 Planned Features

Small box support

Expanded automation logic

Additional ordering safeguards

✨ Final Word

Warehouse Manager v0.9 transforms inventory management into a background system you never have to think about.

Set it. Let it run. Focus on growing your shop.
