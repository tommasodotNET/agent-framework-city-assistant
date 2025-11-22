# Geocoding MCP Server

A Model Context Protocol (MCP) server that provides geocoding functionality for converting addresses and landmarks into geographic coordinates.

## Overview

This MCP server exposes a geocoding tool that can be used by AI agents and other MCP clients to convert location names (addresses, landmarks, cities) into latitude/longitude coordinates.

## Features

- **Mock Geocoding Data**: Includes pre-configured coordinates for Rome landmarks and cities
- **Fallback Handling**: Returns Rome city center coordinates for unknown locations
- **MCP Protocol**: Fully compliant with the Model Context Protocol specification
- **HTTP Transport**: Accessible via HTTP/HTTPS endpoints

## Known Locations

The server includes mock data for the following locations:

### Rome Landmarks
- Colosseum / Coliseum
- Roman Forum
- Vatican / Vatican City
- Pantheon
- Trevi Fountain
- Spanish Steps
- Trastevere

### Cities
- Rome / Roma
- Latina

### Rome Areas
- Downtown Rome
- Rome City Center
- Termini Station

## MCP Tool

### geocode_location

Geocodes an address or landmark to get its coordinates (latitude, longitude).

**Parameters:**
- `location` (string, required): Address or landmark name to geocode

**Returns:**
A JSON object containing:
- `success` (boolean): Whether the geocoding was successful
- `location` (string): The input location name
- `latitude` (number): The latitude coordinate
- `longitude` (number): The longitude coordinate
- `message` (string): Success or error message
- `isFallback` (boolean, optional): True if fallback coordinates were used

**Example Response:**
```json
{
  "success": true,
  "location": "Colosseum",
  "latitude": 41.8902,
  "longitude": 12.4922,
  "message": "Successfully geocoded 'Colosseum' to coordinates"
}
```

## Running the Server

### Development

```bash
dotnet run
```

The server will start on:
- HTTPS: https://localhost:7299
- HTTP: http://localhost:5299

### Production

```bash
dotnet run --configuration Release
```

## MCP Endpoints

The server exposes standard MCP endpoints:

- `/mcp/v1/initialize` - Initialize MCP session
- `/mcp/v1/tools/list` - List available tools
- `/mcp/v1/tools/call` - Call a tool

## Health Check

The server includes a health check endpoint at `/health` (provided by Aspire service defaults).

## Dependencies

- ModelContextProtocol.AspNetCore (0.2.0-preview.1)
- ServiceDefaults (local project reference for Aspire integration)

## Future Enhancements

This server currently uses mock data. Future versions could integrate with real geocoding services such as:
- Azure Maps API
- Google Maps Geocoding API
- OpenStreetMap Nominatim API

## Integration Example

To use this MCP server from an AI agent:

```csharp
// Connect to the MCP server
var mcpClient = await McpClientFactory.CreateAsync(httpTransport);

// Call the geocoding tool
var result = await mcpClient.CallToolAsync(
    "geocode_location",
    new Dictionary<string, object?> { ["location"] = "Colosseum" },
    cancellationToken: CancellationToken.None
);

// Parse the result
var content = result.Content.OfType<TextContentBlock>().First().Text;
var geocodeResult = JsonSerializer.Deserialize<GeocodeResult>(content);
```

## License

Part of the Agent Framework City Assistant project.
