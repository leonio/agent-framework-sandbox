import { useEffect, useState } from "react";
import { api, type Template, type TemplateKind } from "../lib/api";
import { useAction } from "../lib/hooks";
import { Button, Card, ErrorText, Pill, inputClass, timeAgo } from "../components/ui";

const kinds: { kind: TemplateKind; label: string; help: string }[] = [
  { kind: "Instruction", label: "Instructions", help: "System prompt of one agent. The key is the agent's name." },
  { kind: "Prompt", label: "Prompts", help: "Task templates with {{placeholders}}, rendered for each call." },
  { kind: "Skill", label: "Skills", help: "Reusable know-how appended to every agent that lists the skill." },
];

const blank = (kind: TemplateKind): Partial<Template> => ({ key: "", kind, title: "", description: "", content: "", skills: "" });

/** Admin area: edit the instructions, prompts and skills agents are built from. Changes apply to the next agent run. */
export function Admin() {
  const [templates, setTemplates] = useState<Template[]>([]);
  const [selected, setSelected] = useState<Partial<Template>>();
  const { busy, error, run } = useAction();

  const load = () => run(async () => setTemplates(await api.templates()));
  useEffect(() => void load(), []); // eslint-disable-line react-hooks/exhaustive-deps

  const save = () =>
    run(async () => {
      const saved = await api.saveTemplate(selected as Template);
      setSelected(saved);
      setTemplates(await api.templates());
    });

  const remove = () =>
    selected?.id && confirm(`Delete "${selected.key}"?`) &&
    run(async () => {
      await api.deleteTemplate(selected.id!);
      setSelected(undefined);
      setTemplates(await api.templates());
    });

  const reset = () =>
    confirm("Reload every template from the Content folder? Your edits will be overwritten.") &&
    run(async () => {
      await api.resetTemplates();
      setSelected(undefined);
      setTemplates(await api.templates());
    });

  const skillKeys = templates.filter((t) => t.kind === "Skill").map((t) => t.key);

  return (
    <div className="mx-auto max-w-6xl space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">Admin: skills, prompts and instructions</h1>
        <Button variant="secondary" disabled={busy} onClick={reset}>Reset to defaults</Button>
      </div>
      <ErrorText>{error}</ErrorText>
      <div className="flex gap-4">
        <div className="w-72 shrink-0 space-y-4">
          {kinds.map(({ kind, label, help }) => (
            <Card key={kind} title={label} actions={<Button variant="ghost" onClick={() => setSelected(blank(kind))}>+ New</Button>}>
              <p className="mb-2 text-xs text-slate-500">{help}</p>
              <ul className="space-y-0.5 text-sm">
                {templates.filter((t) => t.kind === kind).map((t) => (
                  <li key={t.id}>
                    <button onClick={() => setSelected(t)}
                      className={`w-full rounded px-2 py-1 text-left ${selected?.id === t.id ? "bg-indigo-50 text-indigo-700" : "hover:bg-slate-50"}`}>
                      {t.key} <span className="text-xs text-slate-400">v{t.version}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </Card>
          ))}
        </div>

        <div className="min-w-0 flex-1">
          {!selected ? (
            <Card><p className="text-sm text-slate-500">Select a template to edit, or create a new one.</p></Card>
          ) : (
            <Card title={selected.id ? `Edit ${selected.key}` : `New ${selected.kind}`}
              actions={selected.updatedAt && <span className="text-xs text-slate-400">v{selected.version}, updated {timeAgo(selected.updatedAt)}</span>}>
              <div className="space-y-3">
                <div className="grid grid-cols-2 gap-3">
                  <label className="block text-sm font-medium">Key
                    <input className={inputClass} value={selected.key} onChange={(e) => setSelected({ ...selected, key: e.target.value })} />
                  </label>
                  <label className="block text-sm font-medium">Title
                    <input className={inputClass} value={selected.title} onChange={(e) => setSelected({ ...selected, title: e.target.value })} />
                  </label>
                </div>
                <label className="block text-sm font-medium">Description
                  <input className={inputClass} value={selected.description} onChange={(e) => setSelected({ ...selected, description: e.target.value })} />
                </label>
                {selected.kind === "Instruction" && (
                  <label className="block text-sm font-medium">Skills (comma separated)
                    <input className={inputClass} value={selected.skills} onChange={(e) => setSelected({ ...selected, skills: e.target.value })} />
                    <span className="mt-1 flex flex-wrap gap-1">{skillKeys.map((k) => <Pill key={k}>{k}</Pill>)}</span>
                  </label>
                )}
                <label className="block text-sm font-medium">Content (markdown)
                  <textarea className={`${inputClass} font-mono`} rows={18} value={selected.content}
                    onChange={(e) => setSelected({ ...selected, content: e.target.value })} />
                </label>
                <div className="flex gap-2">
                  <Button disabled={busy || !selected.key || !selected.title || !selected.content} onClick={save}>Save</Button>
                  {selected.id && <Button variant="danger" disabled={busy} onClick={remove}>Delete</Button>}
                </div>
              </div>
            </Card>
          )}
        </div>
      </div>
    </div>
  );
}
