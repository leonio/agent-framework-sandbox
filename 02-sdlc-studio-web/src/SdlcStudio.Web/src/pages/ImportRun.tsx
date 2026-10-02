import { useState } from "react";
import { useNavigate } from "react-router";
import { api } from "../lib/api";
import { useAction } from "../lib/hooks";
import { Button, Card, ErrorText, inputClass } from "../components/ui";

/**
 * The "existing app" entry point: instead of an interview, the app-analyst agent explores the code base
 * with file tools and reverse-engineers the specs. Everything after that (review, plan, develop...) is the
 * same loop as a new idea.
 */
export function ImportRun() {
  const [name, setName] = useState("");
  const [mode, setMode] = useState<"path" | "zip">("path");
  const [path, setPath] = useState("");
  const [file, setFile] = useState<File | null>(null);
  const { busy, error, run } = useAction();
  const navigate = useNavigate();

  const submit = () =>
    run(async () => {
      const r = mode === "path" ? await api.importPath(name, path) : await api.importZip(name, file!);
      navigate(`/runs/${r.id}`);
    });

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <h1 className="text-xl font-semibold">Import an existing app</h1>
      <Card>
        <p className="mb-4 text-sm text-slate-600">
          Point the studio at a folder the API server can read, or upload a .zip of the source. Specs are generated from
          the code, then you refine them in the review phase.
        </p>
        <div className="space-y-3">
          <label className="block text-sm font-medium">
            Name
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} placeholder="Legacy billing app" />
          </label>
          <div className="flex gap-4 text-sm">
            <label className="flex items-center gap-1.5"><input type="radio" checked={mode === "path"} onChange={() => setMode("path")} /> Folder path</label>
            <label className="flex items-center gap-1.5"><input type="radio" checked={mode === "zip"} onChange={() => setMode("zip")} /> Upload .zip</label>
          </div>
          {mode === "path" ? (
            <label className="block text-sm font-medium">
              Folder on the API host
              <input className={inputClass} value={path} onChange={(e) => setPath(e.target.value)} placeholder="C:\src\my-app" />
            </label>
          ) : (
            <input type="file" accept=".zip" onChange={(e) => setFile(e.target.files?.[0] ?? null)} className="text-sm" />
          )}
          <ErrorText>{error}</ErrorText>
          <Button disabled={busy || !name || (mode === "path" ? !path : !file)} onClick={submit}>
            {busy ? "Starting..." : "Generate specs from the code"}
          </Button>
        </div>
      </Card>
    </div>
  );
}
