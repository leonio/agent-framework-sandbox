# Sample data

- `reports/`: four example incident reports (alert, log excerpt, customer ticket, latency alert). Type `END` at the paste prompt, or pass `--reports SampleData/reports`, to use them.
- `demo-repo/`: a tiny two-service "repository" (checkout-api, search-api) used when you don't give a repo URL, or when cloning fails (offline). It is indexed for code RAG exactly like a real clone. It contains a planted bug that matches the sample reports.
- `demo-commits.txt`: stands in for `git log` on the demo repo, which has no `.git` folder.

The C# files under `demo-repo/` are data, excluded from compilation in `IncidentTriage.csproj`.
