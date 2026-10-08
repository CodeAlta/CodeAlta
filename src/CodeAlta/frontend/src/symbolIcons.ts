import {
  Anchor, Atom, Binary, Bird, Bot, Box, Braces, Brain, Briefcase, Building2, Cable, Cat, Cloud, Code, Compass, Container, Cpu, Crown,
  Database, Diamond, Dog, Factory, Fish, Flame, FlaskConical, Gem, Ghost, Globe, GraduationCap, HardDrive, Heart, Hexagon, House, Infinity as InfinityIcon,
  KeyRound, Laptop, Layers, Leaf, Lock, Microscope, Monitor, Moon, Mountain, Network, Plug, Puzzle, Rabbit, RadioTower, Rocket, Satellite,
  Server, Shield, Sparkles, Star, Sun, Telescope, Terminal, TestTube, TreePine, WandSparkles, Workflow, Zap, type LucideIcon,
} from "lucide-react";

/**
 * The general icons a provider can be given when no brand fits: a local server, a proxy of a company, a
 * provider of one's own. They are icons of the set the app draws everything else with, by the names of that set.
 */
export const symbolIcons = {
  anchor: Anchor, atom: Atom, binary: Binary, bird: Bird, bot: Bot, box: Box, braces: Braces, brain: Brain, briefcase: Briefcase,
  "building-2": Building2, cable: Cable, cat: Cat, cloud: Cloud, code: Code, compass: Compass, container: Container, cpu: Cpu, crown: Crown,
  database: Database, diamond: Diamond, dog: Dog, factory: Factory, fish: Fish, flame: Flame, "flask-conical": FlaskConical, gem: Gem, ghost: Ghost,
  globe: Globe, "graduation-cap": GraduationCap, "hard-drive": HardDrive, heart: Heart, hexagon: Hexagon, house: House, infinity: InfinityIcon,
  "key-round": KeyRound, laptop: Laptop, layers: Layers, leaf: Leaf, lock: Lock, microscope: Microscope, monitor: Monitor, moon: Moon,
  mountain: Mountain, network: Network, plug: Plug, puzzle: Puzzle, rabbit: Rabbit, "radio-tower": RadioTower, rocket: Rocket, satellite: Satellite,
  server: Server, shield: Shield, sparkles: Sparkles, star: Star, sun: Sun, telescope: Telescope, terminal: Terminal, "test-tube": TestTube,
  "tree-pine": TreePine, "wand-sparkles": WandSparkles, workflow: Workflow, zap: Zap,
} satisfies Record<string, LucideIcon>;

/** The name of a general icon, as a provider names it with `icon` in the configuration. */
export type SymbolIconName = keyof typeof symbolIcons;

/** Every general icon, by name. */
export const symbolIconNames = (Object.keys(symbolIcons) as SymbolIconName[]).sort();

/** Whether a text is the name of a general icon. */
export function isSymbolIcon(name: string | null | undefined): name is SymbolIconName {
  return typeof name === "string" && Object.hasOwn(symbolIcons, name);
}
