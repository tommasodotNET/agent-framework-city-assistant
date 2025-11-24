using GeocodingMcpServer.Tools;

var builder = WebApplication.CreateBuilder(args);

// Add service defaults for Aspire integration (health checks, telemetry, etc.)
builder.AddServiceDefaults();

// Configure MCP server with HTTP transport and geocoding tool
// MCP (Model Context Protocol) enables standardized communication between AI agents and tools
builder.Services.AddMcpServer()
    .WithHttpTransport()  // Enable HTTP-based MCP communication
    .WithTools<GeocodingTool>();  // Register geocoding tool for address/landmark to coordinate conversion

var app = builder.Build();

// Map default endpoints (health checks, metrics, etc.)
app.MapDefaultEndpoints();

// Map MCP protocol endpoints
app.MapMcp("/mcp");

app.Run();
