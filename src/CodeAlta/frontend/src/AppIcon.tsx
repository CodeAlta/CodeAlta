import type { ComponentProps } from "react";
import {
  ArrowDown,
  Bot,
  Brain,
  Check,
  CheckCheck,
  ChevronDown,
  CircleAlert,
  Clock3,
  Copy,
  Cpu,
  FileDiff,
  Gauge,
  History,
  Info,
  ListChecks,
  Maximize2,
  NotebookPen,
  RefreshCw,
  Search,
  Send,
  Settings,
  ShieldQuestion,
  Sparkles,
  SquareTerminal,
  UserRound,
  X,
  type LucideIcon,
} from "lucide-react";

const icons = {
  arrowDown: ArrowDown,
  assistant: Bot,
  brain: Brain,
  check: Check,
  checked: CheckCheck,
  chevronDown: ChevronDown,
  close: X,
  reminder: Clock3,
  copy: Copy,
  error: CircleAlert,
  expand: Maximize2,
  file: FileDiff,
  history: History,
  info: Info,
  model: Cpu,
  notes: NotebookPen,
  plan: ListChecks,
  prompt: Sparkles,
  refresh: RefreshCw,
  search: Search,
  send: Send,
  settings: Settings,
  tool: SquareTerminal,
  usage: Gauge,
  user: UserRound,
  question: ShieldQuestion,
} satisfies Record<string, LucideIcon>;

export type IconName = keyof typeof icons;

export function AppIcon({ name, ...props }: { name: IconName } & Omit<ComponentProps<LucideIcon>, "ref">) {
  const Icon = icons[name];
  return <Icon aria-hidden="true" focusable="false" strokeWidth={1.8} {...props} />;
}
