using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Roster.Platform.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "agent_versions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    hash = table.Column<string>(type: "text", nullable: false),
                    version_label = table.Column<string>(type: "text", nullable: false),
                    archetype = table.Column<string>(type: "text", nullable: false),
                    instructions = table.Column<string>(type: "text", nullable: false),
                    skills_json = table.Column<string>(type: "jsonb", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    first_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_agent_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "jobs",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    pool = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    max_attempts = table.Column<int>(type: "integer", nullable: false),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    lease_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    locked_by = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: true),
                    trace_parent = table.Column<string>(type: "text", nullable: true),
                    last_error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_jobs", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "assignments",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    scenario = table.Column<string>(type: "text", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    source = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    model = table.Column<string>(type: "text", nullable: true),
                    overrides_json = table.Column<string>(type: "jsonb", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    error = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_assignments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "invocations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    phase_key = table.Column<string>(type: "text", nullable: false),
                    step_key = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    agent_hash = table.Column<string>(type: "text", nullable: false),
                    endpoint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    endpoint_name = table.Column<string>(type: "text", nullable: false),
                    model = table.Column<string>(type: "text", nullable: false),
                    runtime_kind = table.Column<string>(type: "text", nullable: false),
                    output_strategy = table.Column<string>(type: "text", nullable: false),
                    input_text = table.Column<string>(type: "text", nullable: false),
                    outcome = table.Column<string>(type: "text", nullable: true),
                    output_json = table.Column<string>(type: "jsonb", nullable: true),
                    raw_text = table.Column<string>(type: "text", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    tool_calls_json = table.Column<string>(type: "jsonb", nullable: true),
                    reasoning = table.Column<string>(type: "text", nullable: true),
                    input_tokens = table.Column<long>(type: "bigint", nullable: true),
                    output_tokens = table.Column<long>(type: "bigint", nullable: true),
                    duration_ms = table.Column<long>(type: "bigint", nullable: true),
                    trace_id = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_invocations", x => x.id);
                    table.ForeignKey(
                        name: "fk_invocations_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "phases",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "text", nullable: false),
                    order = table.Column<int>(type: "integer", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    attempt = table.Column<int>(type: "integer", nullable: false),
                    output_json = table.Column<string>(type: "jsonb", nullable: true),
                    error = table.Column<string>(type: "text", nullable: true),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_phases", x => x.id);
                    table.ForeignKey(
                        name: "fk_phases_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retro_sessions",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retro_sessions", x => x.id);
                    table.ForeignKey(
                        name: "fk_retro_sessions_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "run_events",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    payload_json = table.Column<string>(type: "jsonb", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_run_events", x => x.id);
                    table.ForeignKey(
                        name: "fk_run_events_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "findings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    invocation_id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: false),
                    agent_hash = table.Column<string>(type: "text", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    title = table.Column<string>(type: "text", nullable: false),
                    detail = table.Column<string>(type: "text", nullable: false),
                    recommendation = table.Column<string>(type: "text", nullable: false),
                    severity = table.Column<string>(type: "text", nullable: false),
                    file_path = table.Column<string>(type: "text", nullable: true),
                    confidence = table.Column<double>(type: "double precision", nullable: false),
                    decision = table.Column<string>(type: "text", nullable: false),
                    decision_reason = table.Column<string>(type: "text", nullable: true),
                    decided_by_id = table.Column<string>(type: "text", nullable: true),
                    decided_by_name = table.Column<string>(type: "text", nullable: true),
                    decided_by_title = table.Column<string>(type: "text", nullable: true),
                    decided_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_findings", x => x.id);
                    table.ForeignKey(
                        name: "fk_findings_assignments_assignment_id",
                        column: x => x.assignment_id,
                        principalTable: "assignments",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_findings_invocations_invocation_id",
                        column: x => x.invocation_id,
                        principalTable: "invocations",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "retro_cards",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sentiment = table.Column<string>(type: "text", nullable: false),
                    text = table.Column<string>(type: "text", nullable: false),
                    agent_name = table.Column<string>(type: "text", nullable: true),
                    agent_hash = table.Column<string>(type: "text", nullable: true),
                    state = table.Column<string>(type: "text", nullable: false),
                    assisted_by = table.Column<string>(type: "text", nullable: true),
                    author_id = table.Column<string>(type: "text", nullable: true),
                    author_name = table.Column<string>(type: "text", nullable: true),
                    author_title = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retro_cards", x => x.id);
                    table.ForeignKey(
                        name: "fk_retro_cards_retro_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "retro_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "retro_messages",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    in_progress = table.Column<bool>(type: "boolean", nullable: false),
                    invocation_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_retro_messages", x => x.id);
                    table.ForeignKey(
                        name: "fk_retro_messages_retro_sessions_session_id",
                        column: x => x.session_id,
                        principalTable: "retro_sessions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "credentials",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    label = table.Column<string>(type: "text", nullable: false),
                    hint = table.Column<string>(type: "text", nullable: false),
                    @sealed = table.Column<byte[]>(name: "sealed", type: "bytea", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_credentials", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "endpoints",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_id = table.Column<string>(type: "text", nullable: true),
                    name = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    base_url = table.Column<string>(type: "text", nullable: true),
                    default_model = table.Column<string>(type: "text", nullable: false),
                    tier_models_json = table.Column<string>(type: "jsonb", nullable: false),
                    native_structured_output = table.Column<bool>(type: "boolean", nullable: false),
                    supports_tools = table.Column<bool>(type: "boolean", nullable: false),
                    supports_streaming = table.Column<bool>(type: "boolean", nullable: false),
                    reasoning_model = table.Column<bool>(type: "boolean", nullable: false),
                    max_concurrency = table.Column<int>(type: "integer", nullable: false),
                    requests_per_minute = table.Column<int>(type: "integer", nullable: true),
                    credential_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_endpoints", x => x.id);
                    table.ForeignKey(
                        name: "fk_endpoints_credentials_credential_id",
                        column: x => x.credential_id,
                        principalTable: "credentials",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: false),
                    email = table.Column<string>(type: "text", nullable: true),
                    title = table.Column<string>(type: "text", nullable: true),
                    default_endpoint_id = table.Column<Guid>(type: "uuid", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_users", x => x.id);
                    table.ForeignKey(
                        name: "fk_users_endpoints_default_endpoint_id",
                        column: x => x.default_endpoint_id,
                        principalTable: "endpoints",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "ix_agent_versions_agent_name_hash",
                table: "agent_versions",
                columns: new[] { "agent_name", "hash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_assignments_endpoint_id",
                table: "assignments",
                column: "endpoint_id");

            migrationBuilder.CreateIndex(
                name: "ix_assignments_owner_id_created_at",
                table: "assignments",
                columns: new[] { "owner_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ix_credentials_user_id",
                table: "credentials",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ix_endpoints_credential_id",
                table: "endpoints",
                column: "credential_id");

            migrationBuilder.CreateIndex(
                name: "ix_endpoints_owner_id",
                table: "endpoints",
                column: "owner_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_assignment_id",
                table: "findings",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_findings_invocation_id",
                table: "findings",
                column: "invocation_id");

            migrationBuilder.CreateIndex(
                name: "ix_invocations_agent_name_agent_hash",
                table: "invocations",
                columns: new[] { "agent_name", "agent_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_invocations_assignment_id",
                table: "invocations",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_jobs_idempotency_key",
                table: "jobs",
                column: "idempotency_key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_jobs_pool_state_not_before",
                table: "jobs",
                columns: new[] { "pool", "state", "not_before" });

            migrationBuilder.CreateIndex(
                name: "ix_phases_assignment_id_order",
                table: "phases",
                columns: new[] { "assignment_id", "order" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retro_cards_agent_name_agent_hash",
                table: "retro_cards",
                columns: new[] { "agent_name", "agent_hash" });

            migrationBuilder.CreateIndex(
                name: "ix_retro_cards_assignment_id",
                table: "retro_cards",
                column: "assignment_id");

            migrationBuilder.CreateIndex(
                name: "ix_retro_cards_session_id",
                table: "retro_cards",
                column: "session_id");

            migrationBuilder.CreateIndex(
                name: "ix_retro_messages_session_id_sequence",
                table: "retro_messages",
                columns: new[] { "session_id", "sequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_retro_sessions_assignment_id",
                table: "retro_sessions",
                column: "assignment_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_run_events_assignment_id_id",
                table: "run_events",
                columns: new[] { "assignment_id", "id" });

            migrationBuilder.CreateIndex(
                name: "ix_users_default_endpoint_id",
                table: "users",
                column: "default_endpoint_id");

            migrationBuilder.AddForeignKey(
                name: "fk_assignments_endpoints_endpoint_id",
                table: "assignments",
                column: "endpoint_id",
                principalTable: "endpoints",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "fk_assignments_users_owner_id",
                table: "assignments",
                column: "owner_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_credentials_users_user_id",
                table: "credentials",
                column: "user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "fk_endpoints_users_owner_id",
                table: "endpoints",
                column: "owner_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_users_endpoints_default_endpoint_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "agent_versions");

            migrationBuilder.DropTable(
                name: "findings");

            migrationBuilder.DropTable(
                name: "jobs");

            migrationBuilder.DropTable(
                name: "phases");

            migrationBuilder.DropTable(
                name: "retro_cards");

            migrationBuilder.DropTable(
                name: "retro_messages");

            migrationBuilder.DropTable(
                name: "run_events");

            migrationBuilder.DropTable(
                name: "invocations");

            migrationBuilder.DropTable(
                name: "retro_sessions");

            migrationBuilder.DropTable(
                name: "assignments");

            migrationBuilder.DropTable(
                name: "endpoints");

            migrationBuilder.DropTable(
                name: "credentials");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
