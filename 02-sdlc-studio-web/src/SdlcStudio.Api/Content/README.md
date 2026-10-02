# Content: instructions, prompts and skills

These markdown files **seed** the Admin area on first start. After that the database is the
source of truth, so edits made in the Admin UI change agent behaviour immediately (the next agent
that is created reads the new text). Use *Admin → Reset to defaults* to reload these files.

| Folder | Kind | Used for |
| --- | --- | --- |
| `instructions/` | `Instruction` | The system prompt of one agent. File name = agent key. |
| `prompts/` | `Prompt` | Task prompt templates with `{{placeholders}}`, rendered per call. |
| `skills/` | `Skill` | Reusable capabilities. An instruction lists them in front matter (`skills:`) and they are appended to its system prompt. |

WHY split them? Instructions say *who* an agent is, prompts say *what to do now*, skills are
*how-to knowledge* shared by several agents (e.g. both the developer and the code reviewer use
`conventional-commits`). Changing a skill once updates every agent that uses it.

ALTERNATIVES: Prompty files, Semantic Kernel prompt templates (Handlebars/Liquid), or the Agent
Framework's declarative agents (YAML). We use plain markdown + a tiny `{{name}}` renderer so the
mechanics stay visible.
