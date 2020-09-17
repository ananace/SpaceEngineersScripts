/*
 * Ananace's Hinge / Rotor drive script
 * https://github.com/ananace/SpaceEngineersScripts
 *
 * rev @revision@ - @date@
 */

// The prefix for Custom Data
const string CDPrefix = "!acs";

/* Using the script;
 *
 * Build a vehicle of some kind with pistons, rotors, or hinges, attach at least one seat/remote control to it.
 * Configure the moveable blocks that should be controlled using a single-line of Custom Data, some examples are provided below.
 *
 * For advanced use, you can have multiple input profiles, switching between them by running the PB with the wanted profile as the argument.
 * If you start the argument with an '!' you'll toggle the profile, returning back to "primary" if you're already in the named profile.
 *
 * The line should start with the prefix specified above and can include the following configuration keys, separated by spaces;
 *   (no)center - Sets the block to attempt to automatically center itself when not actively moving. (Default on)
 *   (no)inv - Inverts the direction of movement. (Default off)
 *   (no)scale - Scales the valid range of movement based on the vehicle velocity. 100% below 5m/s, and then slowly decreasing to a target value at the target speed. (Default off)
 *   onlypos
 *   onlyneg - These limit the affecting input to only acting on either positive or negative values.
 *
 * For controllers the following keywords are valid; (Also prefixed with the Custom Data prefix)
 *   primary - Will make this the main input whenever it's active.
 *   ignore - Will never make this the main input.
 *
 * Additional values can be set in a similar manner, key and default values specified below;
 *   input={movex,movey,movez,movexz,rotatepitch,rotateyaw,rotateroll} - Sets what input the block should listen to.
 *   speed=10 - The velocity for movement, per second. Deg/s for rotors/hinges, m/s for pistons.
 *   centerpos=(0) - Overrides the center value for the block for when auto-centering, default if not specified is 0 for rotors/hinges, middle of the range for pistons.
 *   scalestart=5 - The speed (of the vehicle) at which the turning should start scaling, in m/s.
 *   scaleend=25 - The speed (of the vehicle) at which the scaling should end, in m/s.
 *   scaleendmod=0.25 - The value modifier at the end of the scale, 0.25 means that the rotor/piston/hinge will only be allowed to move to 25% of its range - from the center position - as the vehicle reaches the scaleend speed.
 *   lock=0 - Locks the hinge/rotor if the target difference is less than the value specified (less than N deg for rotors/hinges, less than N meters for pistons). Can cause shaking when auto-centering is also enabled.
 *   controller=<name> - Limits the block to only acting on the controller matching the given name or containing the name in their custom data under the specified prefix. (Can't contain spaces)
 *   profile=<name> (primary) - Limits the block to only acting when the profile matching the given name is active, default profile is "primary". (Can't contain spaces)
 *   duplicate=<name> - Makes this block use the same input as the given block, can be matched by name or tag - see below. (Can't contain spaces)
 *   tag=<name> - Tags this block with the given tag, to make it easier to use as a source for duplication. (Also can't contain spaces)
 *
 * Some examples, along with a possible use-case for each;
 *
 *   "!acs input=movex center scale" - For car steering using a hinge/rotor, will limit turning at high speeds to avoid rolling.
 *   "!acs input=rotatepitch center speed=90" - For controlling pitch flaps on an airplane.
 *   "!acs input=movez nocenter inv" - For moving a lift up and down using ceiling-mounted pistons.
 *   "!acs input=rotateroll center scale scaleend=25 scaleendmod=0.05" - For handling rotor-mounted roll thrusters, that should only turn to 5% of their angular limit when the vehicle is above 25m/s
 *   "!acs input=movez center onlypos" - Will extend a piston or rotate a rotor/hinge when pressing space, returning it back to zero again when released. Good for deploying air/ground-brakes.
 */

// Default values for the configuration can be set here;
class BlockConfig
{
	public float Speed = 10,
	       ScaleStart = 10,
	       ScaleEnd = 25,
	       ScaleEndMod = 0.25f,
	       Lock = 0;
	public bool Invert = false;
	public bool Center = true;
	public bool Scale = false;
};

bool Debug = false;

// ==-- Script Separator --==

/* Still TODO:
 *
 * - [X] 2D input mode (MoveXZ)
 *   - [ ] Warn on 2D input on pistons/scale
 * - [X] State storage between reloads
 * - [ ] Zero/Center on leaving active controller
 * - [ ] Add a no-holding/relative-only mode
 * - [ ] Cooperative control when running multiple instances on the same vehicle - for rover/trailer separation and the like
 * - [ ] Cooperative control when using multiple simultaneously manned cockpits - main and standby, like in aircraft.
 * - [ ] Further trimming of unnecessary instructions
 *   - [X] When Scanning
 *   - [ ] Store controller data
 *   - [ ] Reduce update rate on locks/non-controlled
 * - [X] Add a profile system for multiple input profiles
 *   - [ ] Support multiple profiles on the same block
 */

IMyShipController MainController;
List<IMyShipController> Controllers = new List<IMyShipController>();
List<BlockData> ManagedBlocks = new List<BlockData>();
const string SpinnerStr = "/-\\|";
StringBuilder BlockInfo = new StringBuilder();
StringBuilder DebugInfo;
Action DebugDump = () => {};
Action<string> RealEcho = s => {};
Action<string> AllEcho = s => {};
string ActiveProfile = "primary";

int ScriptStep = 0, step100=0;
IMyTextPanel _logOutput;

public Program()
{
	Runtime.UpdateFrequency = UpdateFrequency.Update1 | UpdateFrequency.Update100;

	if (Debug)
	{
		DebugInfo = new StringBuilder();
		DebugDump = () => Echo(DebugInfo?.ToString());
	}

	_logOutput = GridTerminalSystem.GetBlockWithName("Log LCD") as IMyTextPanel;
	if (_logOutput != null)
	{
		RealEcho = Echo;
		Echo = EchoToLCD;
		AllEcho = s => { Echo(s); RealEcho(s); };
		_logOutput.ContentType = ContentType.TEXT_AND_IMAGE;
	}
	else
	{
		RealEcho = Echo;
		AllEcho = Echo;
	}

	ScanGrid();
	ScanControllers();

	if (Storage.Any())
		try
		{
			var storageparts = Storage.Split('|');
			if (storageparts.Length > 1)
				ActiveProfile = storageparts.First();
			foreach (var stored in storageparts.Last().Split(' '))
			{
				var parts = stored.Split('=');
				var entityId = long.Parse(parts[0]);
				var data = float.Parse(parts[1]);

				var existing = ManagedBlocks.Find(b => b.Block.EntityId == entityId);
				if (existing != null)
					existing.TargetValue = data;
			}
		}
		catch (Exception ex)
		{
			AllEcho($"Exception {ex} occured when loading, continuing.");
		}
}

public void Save()
{
	Storage = ActiveProfile + "|" + string.Join(" ", ManagedBlocks.Select(b => string.Join("=", b.Block.EntityId, b.TargetValue)));
}

const string SpinStr = "-\\|/";
DateTime last = DateTime.Now;
public void Main(string arg, UpdateType updateSource)
{
	_logOutput?.WriteText(" ", false);

	if (!string.IsNullOrEmpty(arg))
	{
		if (arg[0] == '!')
		{
			arg = arg.Substring(1);
			if (arg == ActiveProfile)
				arg = "primary";
		}
		
		ActiveProfile = arg;
	}

	if ((updateSource & UpdateType.Update100) != 0)
	{
		DebugInfo?.Clear();
		ScanGrid((step100++ % 10) == 0);
		ScanControllers();
	}

	if ((updateSource & UpdateType.Update1) == 0)
		return;

	if (!Controllers.Any())
	{
		AllEcho($"Drive script is running, but no valid controller found, add one to continue.\n");
		return;
	}

	var now = DateTime.Now;
	float dt = (float)(now - last).TotalSeconds;
	last = now;

	var step = ScriptStep++;
	AllEcho($"Hinge/Rotor Drive script is running. Profile: {ActiveProfile} {SpinStr[(step / 100) % 4]}\n\nManaging {ManagedBlocks.Count} block(s)");

	var rate = Controllers.Any(c => c.IsUnderControl) ? 2 : 10;
	if (step % rate == 0)
	{
		BlockInfo.Clear();
		for (int i = 0; i < ManagedBlocks.Count; ++i)
		{
			var blockData = ManagedBlocks[i];
			var scannedData = blockData.Scanned;
			var block = blockData.Block;

			if (scannedData == null)
				continue;

			var controller = blockData.Controller ?? MainController;

			BlockInfo.Append($"- {block.CustomName}");
			if (blockData.DuplicateOf != null)
				BlockInfo.Append($" | Duplicate of {blockData.DuplicateOf.Block.CustomName}");
			else
				BlockInfo.Append($" | {blockData.Input}");

			if (controller != MainController)
				BlockInfo.Append($" | Controlled by {controller.CustomName}");

			if (blockData.Input == InputSource.None && blockData.DuplicateOf == null)
			{
				BlockInfo.AppendLine();
				continue;
			}

			if (!block.Enabled)
			{
				BlockInfo.AppendLine(" | Turned off");
				continue;
			}


			IMyPistonBase piston = block is IMyPistonBase ? (IMyPistonBase)block : null;
			IMyMotorStator motor = block is IMyMotorStator ? (IMyMotorStator)block : null;

			float _cur = 0, inputVal = 0, _min = 0, _max = 0;
			if (piston != null)
			{
				_min = piston.MinLimit;
				_max = piston.MaxLimit;
				_cur = piston.CurrentPosition;
			}
			else
			{
				_min = motor.LowerLimitDeg;
				_max = motor.UpperLimitDeg;
				_cur = MathHelper.ToDegrees(motor.Angle);
			}

			if (blockData.DuplicateOf != null)
			{
				var duplicated = blockData.DuplicateOf;
				blockData.TargetValue = duplicated.TargetValue;

				scannedData.Lock = duplicated.Scanned.Lock;
			}
			else if ((blockData.Profile == null && ActiveProfile == "primary") || blockData.Profile == ActiveProfile)
			{
				if (blockData.Input == InputSource.MoveXZ)
				{
					var inputVector = new Vector2(controller.MoveIndicator.X, controller.MoveIndicator.Z);
					if (inputVector.LengthSquared() > 0)
					{
						inputVal = float.MinValue;
						var targetAngle = MathHelper.ToDegrees(MyMath.ArcTanAngle(inputVector.X, inputVector.Y) + MathHelper.PiOver2);
						blockData.TargetValue = targetAngle;

						BlockInfo.Append($"\n  Mode: 2D Input | Angle: {blockData.TargetValue}");
					}
				}
				else
				{
					switch (blockData.Input)
					{
						case InputSource.MoveX: inputVal = controller.MoveIndicator.X; break;
						case InputSource.MoveY: inputVal = controller.MoveIndicator.Y; break;
						case InputSource.MoveZ: inputVal = controller.MoveIndicator.Z; break;
						case InputSource.RotatePitch: inputVal = controller.RotationIndicator.X; break;
						case InputSource.RotateYaw: inputVal = controller.RotationIndicator.Y; break;
						case InputSource.RotateRoll: inputVal = controller.RollIndicator; break;
					}

					if ((inputVal > 0 && blockData.Limit == InputLimit.NegativeOnly) ||
				    	    (inputVal < 0 && blockData.Limit == InputLimit.PositiveOnly))
						inputVal = 0;
				}
			}
			else
				BlockInfo.Append($"\n  Not active in current profile");

			if (scannedData.Lock > 0 && motor != null)
			{
				var _lock = Math.Abs(_cur - blockData.TargetValue) < scannedData.Lock;
				motor.RotorLock = _lock;

				if (_lock)
					BlockInfo.Append($" | Locked");
			}

			if (blockData.DuplicateOf == null)
				if (inputVal == 0 && scannedData.Center)
				{
					float defaultCenter = (piston != null ? _min + (_max - _min) * 0.5f : 0);
					blockData.TargetValue = MathHelper.Smooth(blockData.CenterPos ?? defaultCenter, blockData.TargetValue);

					BlockInfo.Append($"\n  Mode: Centering");
				}
				else if (blockData.Input != InputSource.MoveXZ)
				{
					float min = _min, max = _max, scale = 1;
					if (scannedData.Scale)
					{
						float speed = (float)MainController.GetShipSpeed();
						if (speed > scannedData.ScaleEnd)
							scale = scannedData.ScaleEndMod;
						else if (speed > scannedData.ScaleStart)
							scale = MathHelper.Lerp(1, scannedData.ScaleEndMod, (speed - scannedData.ScaleStart) / (scannedData.ScaleEnd - scannedData.ScaleStart));

						min = _min * scale;
						max = _max * scale;
					}

					inputVal *= (scannedData.Invert ? -scannedData.Speed : scannedData.Speed) * MathHelper.Pi * dt * scale;

					blockData.TargetValue = MathHelper.Clamp(blockData.TargetValue + inputVal, min, max);

					// Ensure values don't overflow on rotors
					if (motor != null)
						blockData.TargetValue = MathHelper.ToDegrees(MathHelper.WrapAngle(MathHelper.ToRadians(blockData.TargetValue)));

					BlockInfo.Append($"\n  Mode: {(piston != null ? "Moving" : "Turning")}");
					if (scannedData.Scale)
						BlockInfo.Append($" | Scale: {(scale * 100).ToString("N0")}%");
				}

			float targetVal;
			if (piston != null)
				targetVal = blockData.TargetValue - _cur;
			else
			{
				var angDiff = blockData.TargetValue - _cur;

				if (angDiff <= -180)
					angDiff += 360;
				else if (angDiff >= 180)
					angDiff -= 360;

				targetVal = angDiff;
			}

			var unit = (piston != null ? "m/s" : "RPM");
			BlockInfo.Append($"\n  Target position: {blockData.TargetValue.ToString("N2")}");
			BlockInfo.Append($"\n  Target velocity: {targetVal.ToString("N2")} {unit}");

			if (piston != null)
				piston.Velocity = targetVal;
			else if (!motor.RotorLock)
				motor.TargetVelocityRPM = targetVal;

			BlockInfo.AppendLine();
		}
	}

	Echo(BlockInfo.ToString().TrimEnd());

	if (Debug)
		DebugDump();
	else
		Echo("");

	AllEcho($"Performance:\n  Instructions: {Runtime.CurrentInstructionCount} / {Runtime.MaxInstructionCount}\n  Runtime: {Runtime.LastRunTimeMs} ms");

}

List<IMyFunctionalBlock> scannedObjects = new List<IMyFunctionalBlock>();
List<IMyPistonBase> pistons = new List<IMyPistonBase>();
List<IMyMotorStator> motors = new List<IMyMotorStator>();
void ScanGrid(bool force = false)
{
	GridTerminalSystem.GetBlocksOfType(pistons, b => HasCD(b));
	GridTerminalSystem.GetBlocksOfType(motors, b => HasCD(b));

	scannedObjects.Clear();
	scannedObjects.AddRange(pistons);
	scannedObjects.AddRange(motors);

	for (int i = 0; i < scannedObjects.Count; ++i)
	{
		var block = scannedObjects[i];
		if (GetCDLines(block).All((l) => l.ToLowerInvariant().Contains("ignore")))
			continue;

		var existing = ManagedBlocks.Find(b => b.Block == block);
		if (existing == null)
		{
			existing = new BlockData();
			ManagedBlocks.Add(existing);
		}

		ScanBlock(existing, block, force);
	}

	ManagedBlocks.RemoveAll(managed => managed.Block == null || managed.Block.CubeGrid.GetCubeBlock(managed.Block.Position) == null);

	GridTerminalSystem.GetBlocksOfType(Controllers);
	ScanControllers();
}

List<IMyShipController> potential = new List<IMyShipController>();
void ScanControllers()
{
	potential.Clear();
	for (int i = 0; i < Controllers.Count; ++i)
	{
		var controller = Controllers[i];

		if (!controller.CanControlShip)
			continue;

		var lines = GetCDLines(controller);
		if (lines.Any())
			foreach (var line in lines)
			{
				var lowercase = line.ToLowerInvariant();
				if (lowercase.Contains("primary"))
				{
					MainController = controller;
					return;
				}

				if (lowercase.Contains("ignore"))
					continue;
			}

		potential.Add(controller);
	}

	MainController = potential.FirstOrDefault();
	if (MainController == null)
		MainController = Controllers.FirstOrDefault();
}

IMyShipController FindController(string _tag)
{
	var tag = _tag.ToLower();
	return Controllers.Find(c => c.CustomName.ToLower().Contains(tag) || (HasCD(c) && GetCDLines(c).Any((l) => l.Contains(tag))));
}
BlockData FindManagedBlock(string _tag)
{
	var tag = _tag.ToLower();
	return ManagedBlocks.Find(b => b.Block.CustomName.ToLower().Contains(tag) || b.Tag.ToLower() == tag);
}

bool HasCD(IMyTerminalBlock block)
{
	return block.CustomData.ToLower().Contains(CDPrefix);
}

string[] GetCDLines(IMyTerminalBlock block)
{
	var data = block.CustomData.ToLower();
	if (!data.Contains(CDPrefix))
		return new string[0];

	var split = data.Split("\n".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
	return Array.FindAll(split, (s) => s.StartsWith(CDPrefix));
}

void ScanBlock(BlockData data, IMyFunctionalBlock block, bool force = false)
{
	data.Block = block;

	var lines = GetCDLines(block);
	string line = Array.Find(lines, (l) => {
		if (string.IsNullOrEmpty(data.Profile))
			return true;
		return l.Contains(data.Profile) || (data.Profile == "primary" && !l.Contains("profile="));
	});

	if (line == null)
	{
		data.Scanned = null;
		return;
	}

	int hash = line.GetHashCode();
	if (hash == data.CDHash && !force)
		return;

	data.CDHash = hash;
	var ret = data.Scanned = new BlockConfig();

	try
	{
		var parts = line.Split(" ".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
		for (int i = 0; i < parts.Length; ++i)
		{
			var part = parts[i];
			var subparts = part.Split("=".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
			string key = subparts.First().ToLower();
			string val = subparts.LastOrDefault();

			switch (key)
			{
				case "!acs": break;

					     // Toggles
				case "center": ret.Center = true; break;
				case "nocenter": ret.Center = false; break;
				case "inv": ret.Invert = true; break;
				case "noinv": ret.Invert = false; break;
				case "scale": ret.Scale = true; break;
				case "noscale": ret.Scale = false; break;
				case "onlypos": data.Limit = InputLimit.PositiveOnly; break;
				case "onlyneg": data.Limit = InputLimit.NegativeOnly; break;

						// Parameters
				case "input": data.Input = (InputSource)Enum.Parse(typeof(InputSource), val, true); break;
				case "speed": ret.Speed = Single.Parse(val); break;
				case "centerpos": data.CenterPos = Single.Parse(val); break;
				case "scalestart": ret.ScaleStart = Single.Parse(val); break;
				case "scaleend": ret.ScaleEnd = Single.Parse(val); break;
				case "scaleendmod": ret.ScaleEndMod = Single.Parse(val); break;
				case "lock": ret.Lock = Single.Parse(val); break;

				case "controller": {
							   var found = FindController(val);
							   if (found == null)
								   DebugEcho($"D|{block.CustomName}] Unable to find controller '{val}', ignoring");

							   data.Controller = found;
						   } break;
				case "duplicate": {
							  var found = FindManagedBlock(val);
							  if (found == null)
								  DebugEcho($"D|{block.CustomName}] Unable to find duplicate source '{val}', ignoring");

							  data.DuplicateOf = found;
						  } break;
				case "tag": data.Tag = val; break;
				case "profile": data.Profile = val; break;

				default: DebugEcho($"D|{block.CustomName}] Found unknown key {key}"); break;
			}
		}
	}
	catch (Exception ex)
	{
		DebugEcho($"E|{block.CustomName}] Failed to parse custom data; {ex}");
	}
}

void DebugEcho(string msg)
{
	DebugInfo?.AppendLine(msg);
}

public void EchoToLCD(string text)
{
	_logOutput?.WriteText($"{text}\n", true);
}

enum InputSource
{
	None,

	MoveX,
	MoveY,
	MoveZ,

	MoveXZ,

	RotatePitch,
	RotateYaw,
	RotateRoll
}

enum InputLimit
{
	None,

	PositiveOnly,
	NegativeOnly
}

class BlockData
{
	public float TargetValue = 0;
	public float? CenterPos = null;
	public string Tag = "";
	public string Profile = null;
	public int? CDHash = null;
	public InputSource Input = InputSource.None;
	public InputLimit Limit = InputLimit.None;
	public BlockConfig Scanned = null;
	public IMyFunctionalBlock Block = null;
	public IMyShipController Controller = null;
	public BlockData DuplicateOf = null;
}
