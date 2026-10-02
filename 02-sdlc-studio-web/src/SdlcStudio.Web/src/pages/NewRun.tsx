import { useState } from "react";
import { useNavigate } from "react-router";
import { api } from "../lib/api";
import { useAction } from "../lib/hooks";
import { Button, Card, ErrorText, inputClass } from "../components/ui";

export function NewRun() {
  const [name, setName] = useState("");
  const [idea, setIdea] = useState("");
  const { busy, error, run } = useAction();
  const navigate = useNavigate();

  const submit = () => run(async () => navigate(`/runs/${(await api.start(name, idea)).id}`));

  return (
    <div className="mx-auto max-w-2xl space-y-4">
      <h1 className="text-xl font-semibold">New app idea</h1>
      <Card>
        <p className="mb-4 text-sm text-slate-600">
          Describe the app in a sentence or two. The <b>interviewer agent</b> will then "grill" you one question at a time
          (each with a recommended answer) until the requirements are clear. From there the specs, review, plan,
          development and testing phases follow, with you approving each step.
        </p>
        <div className="space-y-3">
          <label className="block text-sm font-medium">
            Name
            <input className={inputClass} value={name} onChange={(e) => setName(e.target.value)} placeholder="Todo Tracker" />
          </label>
          <label className="block text-sm font-medium">
            Initial idea
            <textarea className={inputClass} rows={5} value={idea} onChange={(e) => setIdea(e.target.value)}
              placeholder="A simple web app where individuals capture tasks with due dates and tick them off." />
          </label>
          <ErrorText>{error}</ErrorText>
          <Button disabled={busy || !name || !idea} onClick={submit}>{busy ? "Starting..." : "Start the interview"}</Button>
        </div>
      </Card>
    </div>
  );
}
