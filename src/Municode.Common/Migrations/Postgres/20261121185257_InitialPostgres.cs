using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Municode.Common.Migrations.Postgres
{
    /// <inheritdoc />
    public partial class InitialPostgres : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bootstrap_records",
                columns: table => new
                {
                    installation_id = table.Column<string>(type: "text", nullable: false),
                    operation_id = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    setup_token_hash = table.Column<string>(type: "text", nullable: true),
                    setup_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    kratos_identity_id = table.Column<string>(type: "text", nullable: true),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    organization_id = table.Column<string>(type: "text", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bootstrap_records", x => x.installation_id);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_gateway_routes",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    loopback_port = table.Column<int>(type: "integer", nullable: false),
                    t3_credential_reference = table.Column<string>(type: "text", nullable: true),
                    registered_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_gateway_routes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_gateway_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    service = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: true),
                    api_key_id = table.Column<string>(type: "text", nullable: true),
                    launch_digest = table.Column<string>(type: "text", nullable: false),
                    session_digest = table.Column<string>(type: "text", nullable: true),
                    launch_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    session_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    redeemed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_gateway_sessions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_credential_cleanups",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    store_provider = table.Column<string>(type: "text", nullable: false),
                    path = table.Column<string>(type: "text", nullable: false),
                    version = table.Column<string>(type: "text", nullable: true),
                    not_before = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_credential_cleanups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "provider_plan_names",
                columns: table => new
                {
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    retired = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_plan_names", x => new { x.organization_id, x.normalized_name });
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    kratos_identity_id = table.Column<string>(type: "text", nullable: true),
                    email = table.Column<string>(type: "text", nullable: false),
                    display_name = table.Column<string>(type: "text", nullable: true),
                    keep_signed_in = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "api_keys",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    key_prefix = table.Column<string>(type: "text", nullable: false),
                    key_hash = table.Column<string>(type: "text", nullable: false),
                    scopes_json = table.Column<string>(type: "text", nullable: false),
                    last_used_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_keys", x => x.id);
                    table.ForeignKey(
                        name: "FK_api_keys_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "audit_events",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    actor = table.Column<string>(type: "text", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    details = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_events", x => x.id);
                    table.ForeignKey(
                        name: "FK_audit_events_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "github_app_connections",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    owner_login = table.Column<string>(type: "text", nullable: false),
                    owner_type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    setup_actor_id = table.Column<string>(type: "text", nullable: true),
                    setup_state_hash = table.Column<string>(type: "text", nullable: true),
                    conversion_code_hash = table.Column<string>(type: "text", nullable: true),
                    setup_expires_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    installation_state_hash = table.Column<string>(type: "text", nullable: true),
                    installation_actor_id = table.Column<string>(type: "text", nullable: true),
                    installation_expires_at_ticks = table.Column<long>(type: "bigint", nullable: false),
                    installation_state_used = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    app_id = table.Column<long>(type: "bigint", nullable: true),
                    owner_id = table.Column<long>(type: "bigint", nullable: true),
                    app_slug = table.Column<string>(type: "text", nullable: true),
                    installation_id = table.Column<long>(type: "bigint", nullable: true),
                    secret_provider = table.Column<string>(type: "text", nullable: true),
                    secret_path = table.Column<string>(type: "text", nullable: true),
                    secret_version = table.Column<string>(type: "text", nullable: true),
                    credential_generation = table.Column<string>(type: "text", nullable: false),
                    repositories_json = table.Column<string>(type: "text", nullable: false),
                    failure_code = table.Column<string>(type: "text", nullable: true),
                    last_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_app_connections", x => x.id);
                    table.ForeignKey(
                        name: "FK_github_app_connections_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provider_accounts",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    provider_type = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    project_id = table.Column<string>(type: "text", nullable: true),
                    default_os_id = table.Column<int>(type: "integer", nullable: true),
                    credential_store_provider = table.Column<string>(type: "text", nullable: false),
                    credential_path = table.Column<string>(type: "text", nullable: false),
                    credential_version = table.Column<string>(type: "text", nullable: true),
                    credential_generation = table.Column<string>(type: "text", nullable: false),
                    validation_status = table.Column<string>(type: "text", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_accounts", x => x.id);
                    table.UniqueConstraint("AK_provider_accounts_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_provider_accounts_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "browser_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    kratos_identity_id = table.Column<string>(type: "text", nullable: false),
                    kratos_session_id = table.Column<string>(type: "text", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    csrf_token_hash = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_seen_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    idle_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    renewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    persistent = table.Column<bool>(type: "boolean", nullable: false),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_browser_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_browser_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    user_id = table.Column<string>(type: "text", nullable: false),
                    role = table.Column<string>(type: "text", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cli_login_sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: true),
                    approved_by_user_id = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    scopes_json = table.Column<string>(type: "text", nullable: false),
                    api_key_id = table.Column<string>(type: "text", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    approved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    exchanged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cli_login_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_api_keys_api_key_id",
                        column: x => x.api_key_id,
                        principalTable: "api_keys",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id");
                    table.ForeignKey(
                        name: "FK_cli_login_sessions_users_approved_by_user_id",
                        column: x => x.approved_by_user_id,
                        principalTable: "users",
                        principalColumn: "id");
                });

            migrationBuilder.CreateTable(
                name: "github_webhook_receipts",
                columns: table => new
                {
                    connection_id = table.Column<string>(type: "text", nullable: false),
                    delivery_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_github_webhook_receipts", x => new { x.connection_id, x.delivery_id });
                    table.ForeignKey(
                        name: "FK_github_webhook_receipts_github_app_connections_connection_id",
                        column: x => x.connection_id,
                        principalTable: "github_app_connections",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "development_workspaces",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    created_by = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    idle_timeout_minutes = table.Column<int>(type: "integer", nullable: true),
                    keep_running = table.Column<bool>(type: "boolean", nullable: false),
                    primary_vm_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    volume_size_gb = table.Column<int>(type: "integer", nullable: false),
                    provider_volume_id = table.Column<string>(type: "text", nullable: true),
                    volume_status = table.Column<string>(type: "text", nullable: false),
                    volume_filesystem = table.Column<string>(type: "text", nullable: false),
                    volume_filesystem_uuid = table.Column<string>(type: "text", nullable: true),
                    volume_attached_vm_id = table.Column<string>(type: "text", nullable: true),
                    last_backup_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    backup_status = table.Column<string>(type: "text", nullable: true),
                    storage_monthly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    currency = table.Column<string>(type: "text", nullable: false),
                    pricing_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspaces", x => x.id);
                    table.UniqueConstraint("AK_development_workspaces_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_development_workspaces_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspaces_provider_accounts_organization_id_pr~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_plans",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    normalized_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    image = table.Column<string>(type: "text", nullable: true),
                    build_platform = table.Column<string>(type: "text", nullable: false),
                    enabled = table.Column<bool>(type: "boolean", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    provisioning_locked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_plans", x => x.id);
                    table.UniqueConstraint("AK_provider_plans_organization_id_id", x => new { x.organization_id, x.id });
                    table.ForeignKey(
                        name: "FK_provider_plans_provider_accounts_organization_id_provider_a~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_vms",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    idempotency_key = table.Column<string>(type: "text", nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    desired_state = table.Column<string>(type: "text", nullable: false),
                    phase = table.Column<string>(type: "text", nullable: false),
                    image_id = table.Column<string>(type: "text", nullable: false),
                    profile = table.Column<string>(type: "text", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    provider_image = table.Column<string>(type: "text", nullable: false),
                    image_manifest_sha256 = table.Column<string>(type: "text", nullable: false),
                    architecture = table.Column<string>(type: "text", nullable: false),
                    cpu_count = table.Column<int>(type: "integer", nullable: false),
                    memory_gi_b = table.Column<int>(type: "integer", nullable: false),
                    compute_hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    compute_reserved = table.Column<bool>(type: "boolean", nullable: false),
                    start_after_stop = table.Column<bool>(type: "boolean", nullable: false),
                    start_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    resource_sample_json = table.Column<string>(type: "text", nullable: true),
                    resource_sample_generation = table.Column<long>(type: "bigint", nullable: true),
                    resource_sample_received_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    runtime_capabilities = table.Column<string>(type: "text", nullable: true),
                    next_image_id = table.Column<string>(type: "text", nullable: true),
                    next_profile = table.Column<string>(type: "text", nullable: true),
                    next_plan_id = table.Column<string>(type: "text", nullable: true),
                    operation_id = table.Column<string>(type: "text", nullable: true),
                    allocation_operation_id = table.Column<string>(type: "text", nullable: true),
                    provider_server_id = table.Column<string>(type: "text", nullable: true),
                    provider_ip_id = table.Column<string>(type: "text", nullable: true),
                    provider_firewall_id = table.Column<string>(type: "text", nullable: true),
                    provider_action_id = table.Column<string>(type: "text", nullable: true),
                    ip_address = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_digest = table.Column<string>(type: "text", nullable: true),
                    enrollment_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    runtime_token_hash = table.Column<string>(type: "text", nullable: true),
                    runtime_token_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssh_bootstrap_secret_ref = table.Column<string>(type: "text", nullable: true),
                    runtime_credential_ref = table.Column<string>(type: "text", nullable: true),
                    gateway_ready = table.Column<bool>(type: "boolean", nullable: false),
                    task_secret_references_json = table.Column<string>(type: "text", nullable: true),
                    enrolled_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ssh_host_key = table.Column<string>(type: "text", nullable: true),
                    ssh_host_key_fingerprint = table.Column<string>(type: "text", nullable: true),
                    ssh_host_key_generation = table.Column<long>(type: "bigint", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    last_heartbeat_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    last_activity_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shutdown_deadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    shutdown_reason = table.Column<string>(type: "text", nullable: true),
                    last_ready_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quiesced_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    quiesced_generation = table.Column<long>(type: "bigint", nullable: true),
                    quiesced_operation_id = table.Column<string>(type: "text", nullable: true),
                    quiescence_receipt = table.Column<string>(type: "text", nullable: true),
                    repository = table.Column<string>(type: "text", nullable: true),
                    base_ref = table.Column<string>(type: "text", nullable: true),
                    base_commit = table.Column<string>(type: "text", nullable: true),
                    work_branch = table.Column<string>(type: "text", nullable: true),
                    push_authorized = table.Column<bool>(type: "boolean", nullable: false),
                    repository_credential_ref = table.Column<string>(type: "text", nullable: true),
                    ignored_data_acknowledged_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    git_status = table.Column<string>(type: "text", nullable: false),
                    base_verified_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    base_verified_generation = table.Column<long>(type: "bigint", nullable: true),
                    git_saved_generation = table.Column<long>(type: "bigint", nullable: true),
                    git_saved_operation_id = table.Column<string>(type: "text", nullable: true),
                    git_saved_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    git_save_receipt = table.Column<string>(type: "text", nullable: true),
                    final_head = table.Column<string>(type: "text", nullable: true),
                    verified_remote_ref = table.Column<string>(type: "text", nullable: true),
                    verified_remote_commit = table.Column<string>(type: "text", nullable: true),
                    discard_generation = table.Column<long>(type: "bigint", nullable: true),
                    discard_operation_id = table.Column<string>(type: "text", nullable: true),
                    discard_requested_by = table.Column<string>(type: "text", nullable: true),
                    discard_requested_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_vms", x => x.id);
                    table.UniqueConstraint("AK_development_workspace_vms_workspace_id_id", x => new { x.workspace_id, x.id });
                    table.CheckConstraint("ck_development_vm_kind", "kind IN ('primary', 'ephemeral')");
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_development_workspaces_organizati~",
                        columns: x => new { x.organization_id, x.workspace_id },
                        principalTable: "development_workspaces",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_provider_accounts_organization_id~",
                        columns: x => new { x.organization_id, x.provider_account_id },
                        principalTable: "provider_accounts",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_development_workspace_vms_provider_plans_organization_id_pl~",
                        columns: x => new { x.organization_id, x.plan_id },
                        principalTable: "provider_plans",
                        principalColumns: new[] { "organization_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_filesystem_jobs",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    organization_id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    state = table.Column<string>(type: "text", nullable: false),
                    path_json = table.Column<string>(type: "text", nullable: false),
                    target_device = table.Column<long>(type: "bigint", nullable: true),
                    target_inode = table.Column<long>(type: "bigint", nullable: true),
                    source_job_id = table.Column<string>(type: "text", nullable: true),
                    idempotency_key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    request_digest = table.Column<string>(type: "text", nullable: false),
                    actor_id = table.Column<string>(type: "text", nullable: false),
                    cancel_requested = table.Column<bool>(type: "boolean", nullable: false),
                    result_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    confirmed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    started_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_filesystem_jobs", x => x.id);
                    table.CheckConstraint("ck_development_filesystem_job_kind", "kind IN ('disk_usage', 'directory_deletion')");
                    table.ForeignKey(
                        name: "FK_development_workspace_filesystem_jobs_development_workspace~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_operations",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    kind = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    step = table.Column<string>(type: "text", nullable: false),
                    lease_owner = table.Column<string>(type: "text", nullable: true),
                    lease_token = table.Column<string>(type: "text", nullable: true),
                    lease_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    attempts = table.Column<int>(type: "integer", nullable: false),
                    plan_id = table.Column<string>(type: "text", nullable: false),
                    provider_account_id = table.Column<string>(type: "text", nullable: false),
                    region = table.Column<string>(type: "text", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    image_id = table.Column<string>(type: "text", nullable: false),
                    provider_image = table.Column<string>(type: "text", nullable: false),
                    image_manifest_sha256 = table.Column<string>(type: "text", nullable: false),
                    compute_hourly_rate = table.Column<decimal>(type: "numeric", nullable: false),
                    provider_resource_id = table.Column<string>(type: "text", nullable: true),
                    provider_action_id = table.Column<string>(type: "text", nullable: true),
                    provider_evidence_json = table.Column<string>(type: "text", nullable: true),
                    error_code = table.Column<string>(type: "text", nullable: true),
                    error_message = table.Column<string>(type: "text", nullable: true),
                    revision = table.Column<long>(type: "bigint", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_operations", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_workspace_operations_development_workspace_vms_~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_task_leases",
                columns: table => new
                {
                    id = table.Column<string>(type: "text", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    task_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_task_leases", x => x.id);
                    table.ForeignKey(
                        name: "FK_development_workspace_task_leases_development_workspace_vms~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "development_workspace_vm_resource_samples",
                columns: table => new
                {
                    vm_id = table.Column<string>(type: "text", nullable: false),
                    generation = table.Column<long>(type: "bigint", nullable: false),
                    minute = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    workspace_id = table.Column<string>(type: "text", nullable: false),
                    observed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    cpu_utilization_percent = table.Column<double>(type: "double precision", nullable: true),
                    memory_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    memory_available_bytes = table.Column<long>(type: "bigint", nullable: true),
                    workspace_disk_total_bytes = table.Column<long>(type: "bigint", nullable: true),
                    workspace_disk_available_bytes = table.Column<long>(type: "bigint", nullable: true),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_development_workspace_vm_resource_samples", x => new { x.vm_id, x.generation, x.minute });
                    table.ForeignKey(
                        name: "FK_development_workspace_vm_resource_samples_development_works~",
                        columns: x => new { x.workspace_id, x.vm_id },
                        principalTable: "development_workspace_vms",
                        principalColumns: new[] { "workspace_id", "id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_key_prefix",
                table: "api_keys",
                column: "key_prefix");

            migrationBuilder.CreateIndex(
                name: "IX_api_keys_organization_id_revoked_at",
                table: "api_keys",
                columns: new[] { "organization_id", "revoked_at" });

            migrationBuilder.CreateIndex(
                name: "IX_audit_events_organization_id_created_at",
                table: "audit_events",
                columns: new[] { "organization_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_bootstrap_records_operation_id",
                table: "bootstrap_records",
                column: "operation_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_kratos_session_id",
                table: "browser_sessions",
                column: "kratos_session_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_token_hash",
                table: "browser_sessions",
                column: "token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_user_id",
                table: "browser_sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_browser_sessions_user_id_revoked_at_idle_expires_at",
                table: "browser_sessions",
                columns: new[] { "user_id", "revoked_at", "idle_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_api_key_id",
                table: "cli_login_sessions",
                column: "api_key_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_approved_by_user_id",
                table: "cli_login_sessions",
                column: "approved_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_expires_at",
                table: "cli_login_sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_organization_id",
                table: "cli_login_sessions",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_cli_login_sessions_status",
                table: "cli_login_sessions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_expires_at",
                table: "development_workspace_filesystem_jobs",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_state_created_at",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "state", "created_at" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_vm_id_kind_idempotenc~",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "vm_id", "kind", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_vm_id_state",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "vm_id", "state" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_filesystem_jobs_workspace_id_vm_id",
                table: "development_workspace_filesystem_jobs",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_routes_vm_id_generation_servi~",
                table: "development_workspace_gateway_routes",
                columns: new[] { "vm_id", "generation", "service" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_launch_digest",
                table: "development_workspace_gateway_sessions",
                column: "launch_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_session_digest",
                table: "development_workspace_gateway_sessions",
                column: "session_digest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_gateway_sessions_vm_id_generation",
                table: "development_workspace_gateway_sessions",
                columns: new[] { "vm_id", "generation" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_status_lease_expires_at",
                table: "development_workspace_operations",
                columns: new[] { "status", "lease_expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_vm_id_generation_kind",
                table: "development_workspace_operations",
                columns: new[] { "vm_id", "generation", "kind" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_operations_workspace_id_vm_id",
                table: "development_workspace_operations",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_vm_id_generation_status",
                table: "development_workspace_task_leases",
                columns: new[] { "vm_id", "generation", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_vm_id_generation_task_id",
                table: "development_workspace_task_leases",
                columns: new[] { "vm_id", "generation", "task_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_task_leases_workspace_id_vm_id",
                table: "development_workspace_task_leases",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vm_resource_samples_expires_at",
                table: "development_workspace_vm_resource_samples",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vm_resource_samples_workspace_id_vm_id",
                table: "development_workspace_vm_resource_samples",
                columns: new[] { "workspace_id", "vm_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_desired_state_phase_shutdown_dead~",
                table: "development_workspace_vms",
                columns: new[] { "desired_state", "phase", "shutdown_deadline" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_plan_id",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "plan_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_provider_account_~",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_organization_id_workspace_id",
                table: "development_workspace_vms",
                columns: new[] { "organization_id", "workspace_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_provider_account_id_provider_serv~",
                table: "development_workspace_vms",
                columns: new[] { "provider_account_id", "provider_server_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_repository_work_branch",
                table: "development_workspace_vms",
                columns: new[] { "repository", "work_branch" },
                unique: true,
                filter: "kind = 'ephemeral'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_workspace_id",
                table: "development_workspace_vms",
                column: "workspace_id",
                unique: true,
                filter: "kind = 'primary'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspace_vms_workspace_id_idempotency_key",
                table: "development_workspace_vms",
                columns: new[] { "workspace_id", "idempotency_key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_idempotency_key",
                table: "development_workspaces",
                columns: new[] { "organization_id", "idempotency_key" },
                unique: true,
                filter: "volume_status <> 'deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_name",
                table: "development_workspaces",
                columns: new[] { "organization_id", "name" },
                unique: true,
                filter: "volume_status <> 'deleted'");

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_organization_id_provider_account_id",
                table: "development_workspaces",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_development_workspaces_provider_account_id_provider_volume_~",
                table: "development_workspaces",
                columns: new[] { "provider_account_id", "provider_volume_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_github_app_connections_organization_id",
                table: "github_app_connections",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_organization_id_user_id",
                table: "organization_members",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_user_id",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "IX_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_plans_organization_id_normalized_name",
                table: "provider_plans",
                columns: new[] { "organization_id", "normalized_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provider_plans_organization_id_provider_account_id",
                table: "provider_plans",
                columns: new[] { "organization_id", "provider_account_id" });

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_kratos_identity_id",
                table: "users",
                column: "kratos_identity_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_events");

            migrationBuilder.DropTable(
                name: "bootstrap_records");

            migrationBuilder.DropTable(
                name: "browser_sessions");

            migrationBuilder.DropTable(
                name: "cli_login_sessions");

            migrationBuilder.DropTable(
                name: "development_workspace_filesystem_jobs");

            migrationBuilder.DropTable(
                name: "development_workspace_gateway_routes");

            migrationBuilder.DropTable(
                name: "development_workspace_gateway_sessions");

            migrationBuilder.DropTable(
                name: "development_workspace_operations");

            migrationBuilder.DropTable(
                name: "development_workspace_task_leases");

            migrationBuilder.DropTable(
                name: "development_workspace_vm_resource_samples");

            migrationBuilder.DropTable(
                name: "github_webhook_receipts");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "provider_credential_cleanups");

            migrationBuilder.DropTable(
                name: "provider_plan_names");

            migrationBuilder.DropTable(
                name: "api_keys");

            migrationBuilder.DropTable(
                name: "development_workspace_vms");

            migrationBuilder.DropTable(
                name: "github_app_connections");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "development_workspaces");

            migrationBuilder.DropTable(
                name: "provider_plans");

            migrationBuilder.DropTable(
                name: "provider_accounts");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
