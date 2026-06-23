using NativeEngine;
using Sandbox.Engine;
using Sandbox.Internal;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonNode = System.Text.Json.Nodes.JsonNode;
using JsonObject = System.Text.Json.Nodes.JsonObject;

namespace Editor;

internal static class SboxMcpBridge
{
	const int MaxConsoleEntries = 5000;
	static readonly Logger Log = new( "SboxMcp" );
	static readonly JsonSerializerOptions CompactJson = new() { WriteIndented = false };
	static readonly object Sync = new();
	static readonly object ConsoleSync = new();
	static readonly List<ConsoleEntry> ConsoleEntries = new( MaxConsoleEntries );

	static TcpListener listener;
	static CancellationTokenSource cancellation;
	static Task acceptTask;
	static Task heartbeatTask;
	static string token;
	static string instanceId;
	static string registryFile;
	static int port;
	static long nextConsoleSequence = 1;
	static volatile bool compileInProgress;
	static volatile bool loggerRegistered;
	static DateTime? lastCompileStartedUtc;
	static DateTime? lastCompileFinishedUtc;
	static bool? lastCompileSuccess;

	public static void Start()
	{
		lock ( Sync )
		{
			if ( listener is not null )
			{
				WriteRegistryRecord();
				return;
			}

			cancellation = new CancellationTokenSource();
			instanceId = $"{Process.GetCurrentProcess().Id}-{Guid.NewGuid():N}"[..18];
			token = Convert.ToHexString( RandomNumberGenerator.GetBytes( 32 ) ).ToLowerInvariant();

			listener = new TcpListener( IPAddress.Loopback, 0 );
			listener.Start();
			port = ((IPEndPoint)listener.LocalEndpoint).Port;

			EnsureRegistryDirectory();
			WriteRegistryRecord();

			if ( !loggerRegistered )
			{
				EditorUtility.AddLogger( OnConsoleMessage );
				loggerRegistered = true;
			}

			acceptTask = AcceptLoopAsync( cancellation.Token );
			heartbeatTask = HeartbeatLoopAsync( cancellation.Token );
			Log.Info( $"sbox MCP bridge listening on 127.0.0.1:{port}" );
		}
	}

	public static void Stop()
	{
		lock ( Sync )
		{
			if ( listener is null ) return;

			try
			{
				cancellation?.Cancel();
				listener.Stop();
			}
			catch ( Exception e )
			{
				Log.Warning( e, "Error while stopping sbox MCP bridge" );
			}

			listener = null;
			cancellation?.Dispose();
			cancellation = null;
			acceptTask = null;
			heartbeatTask = null;

			if ( loggerRegistered )
			{
				EditorUtility.RemoveLogger( OnConsoleMessage );
				loggerRegistered = false;
			}

			try
			{
				if ( !string.IsNullOrWhiteSpace( registryFile ) && File.Exists( registryFile ) )
					File.Delete( registryFile );
			}
			catch ( Exception e )
			{
				Log.Warning( e, "Error while removing sbox MCP registry file" );
			}

			registryFile = null;
		}
	}

	[Event( "compile.started" )]
	internal static void OnCompileStarted( CompileGroup group )
	{
		compileInProgress = true;
		lastCompileStartedUtc = DateTime.UtcNow;
	}

	[Event( "compile.complete" )]
	internal static void OnCompileComplete( CompileGroup group )
	{
		compileInProgress = false;
		lastCompileFinishedUtc = DateTime.UtcNow;
		lastCompileSuccess = group?.BuildResult is { } result ? result.Success : !Project.GetCompileDiagnostics().Any( x => x.Severity.ToString() == "Error" );
	}

	static async Task AcceptLoopAsync( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested )
		{
			TcpClient client;

			try
			{
				client = await listener.AcceptTcpClientAsync( ct );
			}
			catch ( OperationCanceledException )
			{
				break;
			}
			catch ( ObjectDisposedException )
			{
				break;
			}
			catch ( Exception e )
			{
				Log.Warning( e, "sbox MCP bridge accept failed" );
				continue;
			}

			_ = Task.Run( () => HandleClientAsync( client, ct ), ct );
		}
	}

	static async Task HandleClientAsync( TcpClient client, CancellationToken ct )
	{
		using ( client )
		{
			try
			{
				client.NoDelay = true;
				await using var stream = client.GetStream();
				using var reader = new StreamReader( stream );
				await using var writer = new StreamWriter( stream ) { AutoFlush = true };
				var line = await reader.ReadLineAsync( ct );

				var response = await HandleLineAsync( line, ct );
				await writer.WriteLineAsync( response.ToJsonString( CompactJson ) );
			}
			catch ( OperationCanceledException )
			{
			}
			catch ( Exception e )
			{
				Log.Warning( e, "sbox MCP bridge client failed" );
			}
		}
	}

	static async Task<JsonObject> HandleLineAsync( string line, CancellationToken ct )
	{
		JsonNode requestId = null;

		try
		{
			if ( string.IsNullOrWhiteSpace( line ) )
				return Error( null, "protocol_error", "Bridge request was empty" );

			if ( JsonNode.Parse( line ) is not JsonObject request )
				return Error( null, "protocol_error", "Bridge request must be a JSON object" );

			requestId = request["id"]?.DeepClone();

			if ( !TokenEquals( GetString( request, "token", null ), token ) )
				return Error( requestId, "auth_failed", "Invalid bridge token" );

			var operation = GetString( request, "operation", null );
			if ( string.IsNullOrWhiteSpace( operation ) )
				return Error( requestId, "invalid_request", "operation is required" );

			var argumentNode = request["arguments"];
			if ( argumentNode is not null && argumentNode is not JsonObject )
				return Error( requestId, "invalid_request", "arguments must be a JSON object" );

			var arguments = argumentNode as JsonObject ?? new JsonObject();
			var result = await DispatchAsync( operation, arguments, ct );
			return Ok( requestId, result );
		}
		catch ( SboxMcpBridgeException e )
		{
			return Error( requestId, e.Code, e.Message, e.Details );
		}
		catch ( JsonException e )
		{
			return Error( requestId, "protocol_error", e.Message );
		}
		catch ( Exception e )
		{
			return Error( requestId, "editor_exception", e.Message );
		}
	}

	static Task<JsonObject> DispatchAsync( string operation, JsonObject arguments, CancellationToken ct )
	{
		return operation switch
		{
			"status" => SboxMcpMainThread.InvokeAsync( Status, TimeSpan.FromSeconds( 5 ) ),
			"console.query" => Task.FromResult( QueryConsole( arguments ) ),
			"compile" => SboxMcpMainThread.InvokeAsync( () => CompileAsync( arguments ), TimeSpan.FromMinutes( 5 ) ),
			"play.start" => SboxMcpMainThread.InvokeAsync( PlayStart, TimeSpan.FromSeconds( 10 ) ),
			"play.stop" => SboxMcpMainThread.InvokeAsync( PlayStop, TimeSpan.FromSeconds( 10 ) ),
			"input" => SboxMcpMainThread.InvokeAsync( () => Input( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"screenshot" => ScreenshotAsync( arguments, ct ),
			"scene.tree" => SboxMcpMainThread.InvokeAsync( () => SceneTree( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"object.get" => SboxMcpMainThread.InvokeAsync( () => ObjectGet( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"property.set" => SboxMcpMainThread.InvokeAsync( () => PropertySet( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"component.add" => SboxMcpMainThread.InvokeAsync( () => ComponentAdd( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"component.remove" => SboxMcpMainThread.InvokeAsync( () => ComponentRemove( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"selection.get" => SboxMcpMainThread.InvokeAsync( () => SelectionGet( arguments ), TimeSpan.FromSeconds( 10 ) ),
			"selection.set" => SboxMcpMainThread.InvokeAsync( () => SelectionSet( arguments ), TimeSpan.FromSeconds( 10 ) ),
			_ => throw new SboxMcpBridgeException( "unknown_operation", $"Unknown operation: {operation}" )
		};
	}

	static JsonObject Status()
	{
		var diagnostics = DiagnosticsJson();
		var project = Project.Current;
		return new JsonObject
		{
			["instance_id"] = instanceId,
			["project_state"] = project is null ? "loading" : "loaded",
			["project_path"] = project?.GetRootPath() ?? string.Empty,
			["project_title"] = project?.Config?.Title ?? project?.Config?.Ident ?? string.Empty,
			["project_ident"] = project?.Config?.FullIdent ?? string.Empty,
			["play_state"] = Game.IsPlaying ? "playing" : "stopped",
			["active_scene"] = SceneEditorSession.Active?.Scene?.Name ?? string.Empty,
			["compile_state"] = compileInProgress ? "compiling" : "idle",
			["last_compile_success"] = lastCompileSuccess,
			["last_compile_started_utc"] = lastCompileStartedUtc?.ToString( "O" ),
			["last_compile_finished_utc"] = lastCompileFinishedUtc?.ToString( "O" ),
			["diagnostics"] = diagnostics
		};
	}

	static JsonObject QueryConsole( JsonObject arguments )
	{
		var queryTimeUtc = DateTime.UtcNow;
		var minLevel = GetOptionalStringArgument( arguments, "min_level" );
		var exactLevel = GetOptionalStringArgument( arguments, "level" );
		var logger = GetOptionalStringArgument( arguments, "logger" );
		var loggerMatch = GetStringArgument( arguments, "logger_match", "contains" );
		var text = GetOptionalStringArgument( arguments, "text" );
		var afterSequence = GetLongArgument( arguments, "after_sequence", 0 );
		var afterUtc = GetOptionalUtcDateTime( arguments, "after_utc" );
		var beforeUtc = GetOptionalUtcDateTime( arguments, "before_utc" );
		var windowSeconds = GetOptionalIntArgument( arguments, "window_seconds" );
		var limit = Math.Clamp( GetIntArgument( arguments, "limit", 100 ), 1, 1000 );
		var order = GetStringArgument( arguments, "order", "oldest" );

		var minLevelRank = string.IsNullOrWhiteSpace( minLevel ) ? (int?)null : LevelRankOrThrow( minLevel, "min_level" );
		var exactLevelNormalized = string.IsNullOrWhiteSpace( exactLevel ) ? null : NormalizeLevelOrThrow( exactLevel, "level" );

		if ( loggerMatch is not "contains" and not "exact" )
			throw new SboxMcpBridgeException( "invalid_argument", "logger_match must be 'contains' or 'exact'" );
		if ( order is not "oldest" and not "newest" )
			throw new SboxMcpBridgeException( "invalid_argument", "order must be 'oldest' or 'newest'" );

		if ( windowSeconds is not null )
		{
			if ( windowSeconds < 1 || windowSeconds > 86400 )
				throw new SboxMcpBridgeException( "invalid_argument", "window_seconds must be between 1 and 86400" );

			var windowStart = queryTimeUtc.AddSeconds( -windowSeconds.Value );
			afterUtc = afterUtc is null || windowStart > afterUtc.Value ? windowStart : afterUtc;
		}

		if ( afterUtc is not null && beforeUtc is not null && afterUtc > beforeUtc )
			throw new SboxMcpBridgeException( "invalid_argument", "after_utc must be <= before_utc" );

		List<ConsoleEntry> entries;
		lock ( ConsoleSync )
		{
			entries = ConsoleEntries.ToList();
		}

		var oldestSequence = entries.Count == 0 ? 0 : entries.Min( x => x.Sequence );
		var latestSequence = entries.Count == 0 ? 0 : entries.Max( x => x.Sequence );
		var droppedOldEntries = afterSequence > 0 && oldestSequence > 0 && oldestSequence > afterSequence + 1;
		IEnumerable<ConsoleEntry> query = entries.Where( x => x.Sequence > afterSequence );

		if ( minLevelRank is not null )
			query = query.Where( x => LevelRankOrThrow( x.Level, "entry.level" ) >= minLevelRank.Value );

		if ( !string.IsNullOrWhiteSpace( exactLevelNormalized ) )
			query = query.Where( x => string.Equals( NormalizeLevelOrThrow( x.Level, "entry.level" ), exactLevelNormalized, StringComparison.OrdinalIgnoreCase ) );

		if ( afterUtc is not null )
			query = query.Where( x => x.TimeUtc >= afterUtc.Value );

		if ( beforeUtc is not null )
			query = query.Where( x => x.TimeUtc <= beforeUtc.Value );

		if ( !string.IsNullOrWhiteSpace( logger ) )
		{
			query = loggerMatch == "exact"
				? query.Where( x => string.Equals( x.Logger, logger, StringComparison.OrdinalIgnoreCase ) )
				: query.Where( x => x.Logger?.Contains( logger, StringComparison.OrdinalIgnoreCase ) == true );
		}

		if ( !string.IsNullOrWhiteSpace( text ) )
			query = query.Where( x => x.Message?.Contains( text, StringComparison.OrdinalIgnoreCase ) == true || x.Stack?.Contains( text, StringComparison.OrdinalIgnoreCase ) == true );

		var matched = query.ToArray();
		var ordered = order == "newest"
			? matched.OrderByDescending( x => x.Sequence )
			: matched.OrderBy( x => x.Sequence );

		var selected = ordered.Take( limit ).ToArray();
		var nextCursor = selected.Length == 0 ? afterSequence : selected.Max( x => x.Sequence );
		var array = new JsonArray();
		foreach ( var entry in selected )
		{
			array.Add( entry.ToJson() );
		}

		return new JsonObject
		{
			["entries"] = array,
			["count"] = selected.Length,
			["matched_count"] = matched.Length,
			["next_cursor"] = nextCursor,
			["next_sequence"] = nextCursor,
			["oldest_sequence"] = oldestSequence,
			["latest_sequence"] = latestSequence,
			["has_more"] = matched.Length > selected.Length,
			["dropped_old_entries"] = droppedOldEntries,
			["query_time_utc"] = queryTimeUtc.ToString( "O" ),
			["effective_since_utc"] = afterUtc?.ToString( "O" ),
			["effective_until_utc"] = beforeUtc?.ToString( "O" )
		};
	}

	static async Task<JsonObject> CompileAsync( JsonObject arguments )
	{
		if ( Project.Current is null )
			throw new SboxMcpBridgeException( "project_loading", "No project is loaded yet" );

		var alreadyInProgress = compileInProgress;
		if ( alreadyInProgress && !GetBool( arguments, "wait", true ) )
			throw new SboxMcpBridgeException( "compile_in_progress", "Compile is already in progress" );

		compileInProgress = true;
		lastCompileStartedUtc = DateTime.UtcNow;
		var stopwatch = Stopwatch.StartNew();

		try
		{
			var success = await Project.CompileAsync();
			lastCompileSuccess = success;
			lastCompileFinishedUtc = DateTime.UtcNow;
			return new JsonObject
			{
				["success"] = success,
				["compile_state"] = "idle",
				["already_in_progress"] = alreadyInProgress,
				["duration_ms"] = (long)stopwatch.Elapsed.TotalMilliseconds,
				["diagnostics"] = DiagnosticsJson()
			};
		}
		finally
		{
			compileInProgress = false;
		}
	}

	static JsonObject PlayStart()
	{
		if ( Game.IsPlaying )
			return new JsonObject { ["play_state"] = "playing" };

		if ( SceneEditorSession.Active is null )
			throw new SboxMcpBridgeException( "scene_not_loaded", "No editor scene is active" );

		EditorScene.Play();
		return new JsonObject { ["play_state"] = Game.IsPlaying ? "playing" : "stopped" };
	}

	static JsonObject PlayStop()
	{
		if ( Game.IsPlaying )
			EditorScene.Stop();

		return new JsonObject { ["play_state"] = Game.IsPlaying ? "playing" : "stopped" };
	}

	static JsonObject Input( JsonObject arguments )
	{
		if ( !Game.IsPlaying )
			throw new SboxMcpBridgeException( "game_not_running", "Game input requires play mode" );

		var kind = RequiredString( arguments, "kind" );
		if ( kind.StartsWith( "controller_", StringComparison.OrdinalIgnoreCase ) )
			throw new SboxMcpBridgeException( "unsupported_input_kind", "Controller input injection is not supported by this MVP without a real controller device seam" );

		switch ( kind )
		{
			case "key_down":
				SendKey( RequiredString( arguments, "key" ), true );
				break;
			case "key_up":
				SendKey( RequiredString( arguments, "key" ), false );
				break;
			case "key_tap":
				var key = RequiredString( arguments, "key" );
				SendKey( key, true );
				SendKey( key, false );
				break;
			case "mouse_move":
				SendMouseMove( arguments );
				break;
			case "mouse_button":
				InputRouter.OnMouseButton( ResolveMouseButton( RequiredString( arguments, "button" ) ), RequiredString( arguments, "state" ) == "down", 0 );
				break;
			case "mouse_click":
				var button = ResolveMouseButton( RequiredString( arguments, "button" ) );
				InputRouter.OnMouseButton( button, true, 0 );
				InputRouter.OnMouseButton( button, false, 0 );
				break;
			case "mouse_wheel":
				InputRouter.OnMouseWheel( (int)GetDouble( arguments, "x", 0 ), (int)GetDouble( arguments, "y", 0 ), 0 );
				break;
			case "release_all":
				InputRouter.OnWindowActive( false );
				InputRouter.OnWindowActive( true );
				break;
			default:
				throw new SboxMcpBridgeException( "unsupported_input_kind", $"Unsupported input kind: {kind}" );
		}

		return new JsonObject { ["sent"] = true, ["kind"] = kind };
	}

	static async Task<JsonObject> ScreenshotAsync( JsonObject arguments, CancellationToken ct )
	{
		var timeout = Math.Clamp( GetInt( arguments, "timeout", 10 ), 1, 60 );
		var sceneTarget = GetSceneTarget( arguments );
		if ( sceneTarget is not "editor" and not "play" )
			throw new SboxMcpBridgeException( "invalid_argument", $"Unknown scene_target: {sceneTarget}" );
		if ( sceneTarget == "play" && !Game.IsPlaying )
			throw new SboxMcpBridgeException( "game_not_running", "No play scene is active" );

		var path = await SboxMcpMainThread.InvokeAsync( ScreenshotService.RequestCapture, TimeSpan.FromSeconds( 5 ) );
		var deadline = DateTime.UtcNow + TimeSpan.FromSeconds( timeout );

		while ( DateTime.UtcNow < deadline )
		{
			ct.ThrowIfCancellationRequested();
			if ( File.Exists( path ) )
			{
				var dimensions = TryReadPngDimensions( path );
				return new JsonObject
				{
					["path"] = path,
					["width"] = dimensions?.Width,
					["height"] = dimensions?.Height,
					["capture_source"] = "screenshot_service",
					["captured_utc"] = DateTime.UtcNow.ToString( "O" ),
					["play_state"] = Game.IsPlaying ? "playing" : "stopped",
					["scene_target"] = sceneTarget
				};
			}

			await Task.Delay( 50, ct );
		}

		throw new SboxMcpBridgeException( "operation_timeout", "Screenshot was requested but no render frame produced a file before timeout", new JsonObject { ["path"] = path } );
	}

	static JsonObject SceneTree( JsonObject arguments )
	{
		var scene = ResolveScene( arguments );
		var depth = Math.Clamp( GetInt( arguments, "depth", 8 ), 0, 64 );
		var limit = Math.Clamp( GetInt( arguments, "limit", 100 ), 1, 1000 );
		var count = 0;
		var roots = new JsonArray();

		foreach ( var child in scene.Children )
		{
			if ( count >= limit ) break;
			roots.Add( GameObjectTreeJson( child, depth, limit, ref count ) );
		}

		return new JsonObject
		{
			["scene_target"] = GetSceneTarget( arguments ),
			["scene_name"] = scene.Name,
			["objects"] = roots,
			["count"] = count,
			["truncated"] = count >= limit
		};
	}

	static JsonObject ObjectGet( JsonObject arguments )
	{
		var scene = ResolveScene( arguments );
		var go = ResolveGameObject( scene, RequiredGuid( arguments, "object_id" ) );
		var includeProperties = GetBool( arguments, "include_properties", true );

		return new JsonObject
		{
			["object"] = GameObjectJson( go, includeProperties )
		};
	}

	static JsonObject PropertySet( JsonObject arguments )
	{
		var scene = ResolveScene( arguments );
		var go = ResolveGameObject( scene, RequiredGuid( arguments, "object_id" ) );
		var componentId = GetString( arguments, "component_id", null );
		var propertyPath = RequiredString( arguments, "property_path" );
		var value = arguments["value"];
		object target = go;
		Component component = null;

		if ( !string.IsNullOrWhiteSpace( componentId ) )
		{
			component = ResolveComponent( scene, ParseGuid( componentId, "component_id" ) );
			if ( component.GameObject != go )
				throw new SboxMcpBridgeException( "component_not_found", "Component does not belong to object" );

			target = component;
		}

		using var sceneScope = scene.Push();
		using var undo = CreateUndoScope( scene, go, component, "MCP Property Set" );
		var property = ResolveProperty( target, propertyPath );

		if ( !property.IsPublic || !property.IsEditable || !SafeShouldShow( property ) )
			throw new SboxMcpBridgeException( "property_not_writable", $"Property is not editable: {propertyPath}" );

		object converted;
		try
		{
			converted = Json.FromNode( value, property.PropertyType );
		}
		catch ( Exception e )
		{
			throw new SboxMcpBridgeException( "type_conversion_failed", $"Could not convert value for {propertyPath}: {e.Message}" );
		}

		try
		{
			property.Parent.NoteStartEdit( property );
			try
			{
				property.SetValue( converted );
			}
			finally
			{
				property.Parent.NoteFinishEdit( property );
			}
		}
		catch ( Exception e )
		{
			throw new SboxMcpBridgeException( "property_not_writable", $"Could not write property {propertyPath}: {e.Message}" );
		}

		if ( scene.Editor is { } editor )
			editor.HasUnsavedChanges = true;

		return new JsonObject
		{
			["object_id"] = go.Id.ToString(),
			["component_id"] = component?.Id.ToString(),
			["property_path"] = propertyPath,
			["value"] = Json.ToNode( property.GetValue<object>(), property.PropertyType )
		};
	}

	static JsonObject ComponentAdd( JsonObject arguments )
	{
		var scene = ResolveScene( arguments );
		var go = ResolveGameObject( scene, RequiredGuid( arguments, "object_id" ) );
		var componentTypeName = RequiredString( arguments, "component_type" );
		var type = ResolveComponentType( componentTypeName );

		using var sceneScope = scene.Push();
		using var undo = CreateUndoScopeForComponentCreation( scene, "MCP Component Add" );
		var component = go.Components.Create( type );
		if ( component is null )
			throw new SboxMcpBridgeException( "component_add_failed", $"Failed to create component: {componentTypeName}" );

		if ( scene.Editor is { } editor )
			editor.HasUnsavedChanges = true;

		return new JsonObject { ["component"] = ComponentJson( component, true ) };
	}

	static JsonObject ComponentRemove( JsonObject arguments )
	{
		var scene = ResolveScene( arguments );
		var go = ResolveGameObject( scene, RequiredGuid( arguments, "object_id" ) );
		var component = ResolveComponent( scene, RequiredGuid( arguments, "component_id" ) );

		if ( component.GameObject != go )
			throw new SboxMcpBridgeException( "component_not_found", "Component does not belong to object" );

		var removedId = component.Id.ToString();
		using var sceneScope = scene.Push();
		using var undo = CreateUndoScopeForComponentDestruction( scene, component, "MCP Component Remove" );
		component.Destroy();

		if ( scene.Editor is { } editor )
			editor.HasUnsavedChanges = true;

		return new JsonObject { ["removed"] = true, ["component_id"] = removedId };
	}

	static JsonObject SelectionGet( JsonObject arguments )
	{
		var session = ResolveEditorSession( arguments );
		var ids = new JsonArray();

		foreach ( var go in session.Selection.OfType<GameObject>() )
		{
			ids.Add( go.Id.ToString() );
		}

		return new JsonObject { ["object_ids"] = ids };
	}

	static JsonObject SelectionSet( JsonObject arguments )
	{
		var session = ResolveEditorSession( arguments );
		if ( arguments["object_ids"] is not JsonArray ids )
			throw new SboxMcpBridgeException( "invalid_argument", "object_ids must be an array" );

		var selected = new List<GameObject>();
		foreach ( var node in ids )
		{
			var go = ResolveGameObject( session.Scene, ParseGuid( node.GetValue<string>(), "object_ids" ) );
			selected.Add( go );
		}

		using ( session.UndoScope( "MCP Selection Set" ).Push() )
		{
			session.Selection.Clear();
			foreach ( var go in selected )
			{
				session.Selection.Add( go );
			}
		}

		return SelectionGet( arguments );
	}

	static JsonObject DiagnosticsJson()
	{
		var diagnostics = new JsonArray();

		foreach ( var diagnostic in Project.GetCompileDiagnostics().Take( 200 ) )
		{
			diagnostics.Add( new JsonObject
			{
				["id"] = diagnostic.Id,
				["severity"] = diagnostic.Severity.ToString().ToLowerInvariant(),
				["message"] = diagnostic.GetMessage( CultureInfo.InvariantCulture ),
				["location"] = diagnostic.Location?.GetLineSpan().Path ?? string.Empty
			} );
		}

		return new JsonObject { ["items"] = diagnostics, ["count"] = diagnostics.Count };
	}

	static Scene ResolveScene( JsonObject arguments )
	{
		return GetSceneTarget( arguments ) switch
		{
			"editor" => SceneEditorSession.Active?.Scene ?? throw new SboxMcpBridgeException( "scene_not_loaded", "No editor scene is active" ),
			"play" => Game.IsPlaying && Game.ActiveScene is not null ? Game.ActiveScene : throw new SboxMcpBridgeException( "game_not_running", "No play scene is active" ),
			var target => throw new SboxMcpBridgeException( "invalid_argument", $"Unknown scene_target: {target}" )
		};
	}

	static SceneEditorSession ResolveEditorSession( JsonObject arguments )
	{
		if ( GetSceneTarget( arguments ) != "editor" )
			throw new SboxMcpBridgeException( "unsupported_scene_target", "Selection operations are editor-scene only" );

		return SceneEditorSession.Active ?? throw new SboxMcpBridgeException( "scene_not_loaded", "No editor scene is active" );
	}

	static GameObject ResolveGameObject( Scene scene, Guid id )
	{
		return scene.Directory.FindByGuid( id ) ?? throw new SboxMcpBridgeException( "object_not_found", $"GameObject not found: {id}" );
	}

	static Component ResolveComponent( Scene scene, Guid id )
	{
		return scene.Directory.FindComponentByGuid( id ) ?? throw new SboxMcpBridgeException( "component_not_found", $"Component not found: {id}" );
	}

	static TypeDescription ResolveComponentType( string componentTypeName )
	{
		var type = Game.TypeLibrary.GetType<Component>( componentTypeName, true );
		if ( type is null && componentTypeName.LastIndexOf( '.' ) is var index && index > 0 && index < componentTypeName.Length - 1 )
			type = Game.TypeLibrary.GetType<Component>( componentTypeName[(index + 1)..], true );

		if ( type is null || type.TargetType.IsAbstract )
			throw new SboxMcpBridgeException( "component_type_not_found", $"Component type not found: {componentTypeName}" );

		return type;
	}

	static SerializedProperty ResolveProperty( object target, string path )
	{
		var parts = path.Split( '.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries );
		if ( parts.Length == 0 )
			throw new SboxMcpBridgeException( "property_not_found", "property_path is empty" );

		SerializedObject current = EditorUtility.GetSerializedObject( target );
		SerializedProperty property = null;

		for ( var i = 0; i < parts.Length; i++ )
		{
			property = current.GetProperty( parts[i] );
			if ( property is null )
				throw new SboxMcpBridgeException( "property_not_found", $"Property not found: {string.Join( '.', parts.Take( i + 1 ) )}" );

			if ( i == parts.Length - 1 )
				return property;

			if ( !property.TryGetAsObject( out current ) || current is null )
				throw new SboxMcpBridgeException( "property_not_found", $"Property is not traversable: {string.Join( '.', parts.Take( i + 1 ) )}" );
		}

		return property;
	}

	static IDisposable CreateUndoScope( Scene scene, GameObject go, Component component, string name )
	{
		if ( scene.Editor is not SceneEditorSession session || session is GameEditorSession )
			return null;

		var scope = session.UndoScope( name );
		if ( component is not null )
			scope.WithComponentChanges( component );
		else
			scope.WithGameObjectChanges( go, GameObjectUndoFlags.All );

		return scope.Push();
	}

	static IDisposable CreateUndoScopeForComponentCreation( Scene scene, string name )
	{
		if ( scene.Editor is not SceneEditorSession session || session is GameEditorSession )
			return null;

		return session.UndoScope( name ).WithComponentCreations().Push();
	}

	static IDisposable CreateUndoScopeForComponentDestruction( Scene scene, Component component, string name )
	{
		if ( scene.Editor is not SceneEditorSession session || session is GameEditorSession )
			return null;

		return session.UndoScope( name ).WithComponentDestructions( component ).Push();
	}

	static JsonObject GameObjectTreeJson( GameObject go, int depth, int limit, ref int count )
	{
		count++;
		var node = GameObjectJson( go, false );
		var children = new JsonArray();

		if ( depth > 0 )
		{
			foreach ( var child in go.Children )
			{
				if ( count >= limit ) break;
				children.Add( GameObjectTreeJson( child, depth - 1, limit, ref count ) );
			}
		}

		node["children"] = children;
		return node;
	}

	static JsonObject GameObjectJson( GameObject go, bool includeProperties )
	{
		var components = new JsonArray();
		foreach ( var component in go.Components.GetAll() )
		{
			if ( component is null ) continue;
			components.Add( ComponentJson( component, includeProperties ) );
		}

		var obj = new JsonObject
		{
			["id"] = go.Id.ToString(),
			["name"] = go.Name,
			["enabled"] = go.Enabled,
			["parent_id"] = go.Parent is not null and not Scene ? go.Parent.Id.ToString() : null,
			["component_count"] = components.Count,
			["components"] = components
		};

		if ( includeProperties )
			obj["properties"] = PropertiesJson( go );

		return obj;
	}

	static JsonObject ComponentJson( Component component, bool includeProperties )
	{
		var type = Game.TypeLibrary.GetType( component.GetType() );
		var obj = new JsonObject
		{
			["id"] = component.Id.ToString(),
			["type"] = type?.SerializedName ?? component.GetType().FullName,
			["class_name"] = type?.ClassName ?? component.GetType().Name,
			["enabled"] = component.Enabled,
			["active"] = component.Active
		};

		if ( includeProperties )
			obj["properties"] = PropertiesJson( component );

		return obj;
	}

	static JsonArray PropertiesJson( object target )
	{
		var array = new JsonArray();
		SerializedObject serialized;

		try
		{
			serialized = EditorUtility.GetSerializedObject( target );
		}
		catch ( Exception e )
		{
			array.Add( new JsonObject { ["error"] = e.Message } );
			return array;
		}

		foreach ( var property in serialized.Take( 200 ) )
		{
			if ( property is null || !property.IsPublic ) continue;

			var prop = new JsonObject
			{
				["name"] = property.Name,
				["display_name"] = property.DisplayName,
				["type"] = property.PropertyType?.FullName ?? string.Empty,
				["editable"] = property.IsEditable,
				["visible"] = SafeShouldShow( property )
			};

			try
			{
				prop["value"] = Json.ToNode( property.GetValue<object>(), property.PropertyType );
			}
			catch ( Exception e )
			{
				prop["value_error"] = e.Message;
			}

			array.Add( prop );
		}

		return array;
	}

	static bool SafeShouldShow( SerializedProperty property )
	{
		try
		{
			return property.ShouldShow();
		}
		catch
		{
			return true;
		}
	}

	static void SendMouseMove( JsonObject arguments )
	{
		var mode = GetString( arguments, "mode", "absolute" );
		if ( mode == "relative" )
		{
			InputRouter.OnMouseMotion( (float)GetDouble( arguments, "dx", 0 ), (float)GetDouble( arguments, "dy", 0 ) );
			return;
		}

		var x = (float)GetDouble( arguments, "x", InputRouter.MouseCursorPosition.x );
		var y = (float)GetDouble( arguments, "y", InputRouter.MouseCursorPosition.y );
		var delta = new Vector2( x, y ) - InputRouter.MouseCursorPosition;
		InputRouter.OnMousePositionChange( x, y, delta.x, delta.y );
	}

	static void SendKey( string key, bool down )
	{
		var code = ResolveKey( key );
		InputRouter.OnKey( code, code, down, false, 0 );
	}

	static ButtonCode ResolveKey( string key )
	{
		var code = NativeEngine.InputSystem.StringToButtonCode( key );
		if ( code != ButtonCode.BUTTON_CODE_INVALID ) return code;

		var normalized = key.Trim().Replace( " ", "_" ).Replace( "-", "_" ).ToUpperInvariant();
		if ( normalized.Length == 1 && normalized[0] is >= 'A' and <= 'Z' ) normalized = "KEY_" + normalized;
		else if ( normalized.Length == 1 && normalized[0] is >= '0' and <= '9' ) normalized = "KEY_" + normalized;
		else if ( !normalized.StartsWith( "KEY_" ) ) normalized = normalized switch
		{
			"SPACE" => "KEY_SPACE",
			"ENTER" => "KEY_ENTER",
			"ESC" => "KEY_ESCAPE",
			"ESCAPE" => "KEY_ESCAPE",
			"TAB" => "KEY_TAB",
			"BACKSPACE" => "KEY_BACKSPACE",
			"DELETE" => "KEY_DELETE",
			"UP" => "KEY_UP",
			"DOWN" => "KEY_DOWN",
			"LEFT" => "KEY_LEFT",
			"RIGHT" => "KEY_RIGHT",
			"SHIFT" => "KEY_LSHIFT",
			"CTRL" => "KEY_LCONTROL",
			"CONTROL" => "KEY_LCONTROL",
			"ALT" => "KEY_LALT",
			_ => "KEY_" + normalized
		};

		if ( Enum.TryParse<ButtonCode>( normalized, true, out code ) )
			return code;

		throw new SboxMcpBridgeException( "invalid_input", $"Unknown key: {key}" );
	}

	static ButtonCode ResolveMouseButton( string button )
	{
		return button.Trim().ToLowerInvariant() switch
		{
			"left" or "mouse1" or "mouseleft" => ButtonCode.MouseLeft,
			"right" or "mouse2" or "mouseright" => ButtonCode.MouseRight,
			"middle" or "mouse3" or "mousemiddle" => ButtonCode.MouseMiddle,
			"back" or "mouse4" or "mouseback" => ButtonCode.MouseBack,
			"forward" or "mouse5" or "mouseforward" => ButtonCode.MouseForward,
			_ => throw new SboxMcpBridgeException( "invalid_input", $"Unknown mouse button: {button}" )
		};
	}

	static void OnConsoleMessage( LogEvent e )
	{
		lock ( ConsoleSync )
		{
			ConsoleEntries.Add( new ConsoleEntry(
				nextConsoleSequence++,
				e.Level.ToString().ToLowerInvariant(),
				e.Logger ?? string.Empty,
				e.Message ?? e.HtmlMessage ?? e.Exception?.Message ?? string.Empty,
				e.Time == default ? DateTime.UtcNow : e.Time.ToUniversalTime(),
				e.Exception?.GetType().FullName,
				e.Exception?.ToString() ?? e.Stack
			) );

			if ( ConsoleEntries.Count > MaxConsoleEntries )
				ConsoleEntries.RemoveRange( 0, ConsoleEntries.Count - MaxConsoleEntries );
		}
	}

	static JsonObject Ok( JsonNode id, JsonObject result )
	{
		return new JsonObject
		{
			["id"] = id?.DeepClone(),
			["ok"] = true,
			["result"] = result
		};
	}

	static JsonObject Error( JsonNode id, string code, string message, JsonObject details = null )
	{
		return new JsonObject
		{
			["id"] = id?.DeepClone(),
			["ok"] = false,
			["error"] = new JsonObject
			{
				["code"] = code,
				["message"] = message,
				["details"] = details ?? new JsonObject()
			}
		};
	}

	static string GetSceneTarget( JsonObject arguments ) => GetString( arguments, "scene_target", "editor" );

	static string RequiredString( JsonObject obj, string key )
	{
		var value = GetString( obj, key, null );
		if ( string.IsNullOrWhiteSpace( value ) )
			throw new SboxMcpBridgeException( "invalid_argument", $"{key} is required" );

		return value;
	}

	static Guid RequiredGuid( JsonObject obj, string key )
	{
		var value = RequiredString( obj, key );
		return ParseGuid( value, key );
	}

	static Guid ParseGuid( string value, string key )
	{
		if ( !Guid.TryParse( value, out var guid ) )
			throw new SboxMcpBridgeException( "invalid_argument", $"{key} must be a Guid" );

		return guid;
	}

	static string GetString( JsonObject obj, string key, string defaultValue )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<string>() : defaultValue;
	}

	static string GetOptionalStringArgument( JsonObject obj, string key )
	{
		if ( !obj.TryGetPropertyValue( key, out var node ) || node is null )
			return null;

		try
		{
			return node.GetValue<string>();
		}
		catch ( Exception e ) when ( e is InvalidOperationException or FormatException )
		{
			throw InvalidArgumentType( key, "string" );
		}
	}

	static string GetStringArgument( JsonObject obj, string key, string defaultValue )
	{
		return GetOptionalStringArgument( obj, key ) ?? defaultValue;
	}

	static int GetInt( JsonObject obj, string key, int defaultValue )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<int>() : defaultValue;
	}

	static int GetIntArgument( JsonObject obj, string key, int defaultValue )
	{
		if ( !obj.TryGetPropertyValue( key, out var node ) || node is null )
			return defaultValue;

		try
		{
			return node.GetValue<int>();
		}
		catch ( Exception e ) when ( e is InvalidOperationException or FormatException )
		{
			throw InvalidArgumentType( key, "integer" );
		}
	}

	static int? GetOptionalInt( JsonObject obj, string key )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<int>() : null;
	}

	static int? GetOptionalIntArgument( JsonObject obj, string key )
	{
		if ( !obj.TryGetPropertyValue( key, out var node ) || node is null )
			return null;

		try
		{
			return node.GetValue<int>();
		}
		catch ( Exception e ) when ( e is InvalidOperationException or FormatException )
		{
			throw InvalidArgumentType( key, "integer" );
		}
	}

	static long GetLong( JsonObject obj, string key, long defaultValue )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<long>() : defaultValue;
	}

	static long GetLongArgument( JsonObject obj, string key, long defaultValue )
	{
		if ( !obj.TryGetPropertyValue( key, out var node ) || node is null )
			return defaultValue;

		try
		{
			return node.GetValue<long>();
		}
		catch ( Exception e ) when ( e is InvalidOperationException or FormatException )
		{
			throw InvalidArgumentType( key, "integer" );
		}
	}

	static DateTime? GetOptionalUtcDateTime( JsonObject obj, string key )
	{
		var value = GetOptionalStringArgument( obj, key );
		if ( string.IsNullOrWhiteSpace( value ) )
			return null;

		if ( !IsUtcTimestampLiteral( value ) || !DateTimeOffset.TryParse( value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp ) || timestamp.Offset != TimeSpan.Zero )
			throw new SboxMcpBridgeException( "invalid_argument", $"{key} must be an ISO-8601 UTC timestamp" );

		return timestamp.UtcDateTime;
	}

	static bool IsUtcTimestampLiteral( string value )
	{
		return value.EndsWith( "Z", StringComparison.OrdinalIgnoreCase )
			|| value.EndsWith( "+00:00", StringComparison.OrdinalIgnoreCase )
			|| value.EndsWith( "+0000", StringComparison.OrdinalIgnoreCase )
			|| value.EndsWith( "+00", StringComparison.OrdinalIgnoreCase );
	}

	static SboxMcpBridgeException InvalidArgumentType( string argument, string expected )
	{
		return new SboxMcpBridgeException(
			"invalid_argument",
			$"{argument} must be a {expected}",
			new JsonObject { ["argument"] = argument, ["expected"] = expected } );
	}

	static double GetDouble( JsonObject obj, string key, double defaultValue )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<double>() : defaultValue;
	}

	static bool GetBool( JsonObject obj, string key, bool defaultValue )
	{
		return obj.TryGetPropertyValue( key, out var node ) && node is not null ? node.GetValue<bool>() : defaultValue;
	}

	static string NormalizeLevelOrThrow( string level, string argument )
	{
		return level?.ToLowerInvariant() switch
		{
			"trace" => "trace",
			"info" => "info",
			"warn" or "warning" => "warn",
			"error" => "error",
			_ => throw new SboxMcpBridgeException( "invalid_argument", $"{argument} must be trace, info, warn, or error" )
		};
	}

	static int LevelRankOrThrow( string level, string argument )
	{
		return NormalizeLevelOrThrow( level, argument ) switch
		{
			"trace" => 0,
			"info" => 1,
			"warn" => 2,
			"error" => 3,
			_ => throw new SboxMcpBridgeException( "invalid_argument", $"{argument} must be trace, info, warn, or error" )
		};
	}

	static void EnsureRegistryDirectory()
	{
		var dir = RegistryDirectory();
		Directory.CreateDirectory( dir );
		SetUnixMode( dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute );
		registryFile = Path.Combine( dir, $"{instanceId}.json" );
	}

	static async Task HeartbeatLoopAsync( CancellationToken ct )
	{
		while ( !ct.IsCancellationRequested )
		{
			try
			{
				WriteRegistryRecord();
				await Task.Delay( 1000, ct );
			}
			catch ( OperationCanceledException )
			{
				break;
			}
			catch ( Exception e )
			{
				Log.Warning( e, "sbox MCP registry heartbeat failed" );
				await Task.Delay( 1000, ct );
			}
		}
	}

	static void WriteRegistryRecord()
	{
		if ( string.IsNullOrWhiteSpace( registryFile ) ) return;

		var project = Project.Current;
		var process = Process.GetCurrentProcess();
		var record = new JsonObject
		{
			["schema_version"] = 1,
			["instance_id"] = instanceId,
			["pid"] = process.Id,
			["process_start_time_utc"] = process.StartTime.ToUniversalTime().ToString( "O" ),
			["project_path"] = project?.GetRootPath() ?? string.Empty,
			["project_title"] = project?.Config?.Title ?? project?.Config?.Ident ?? string.Empty,
			["host"] = "127.0.0.1",
			["port"] = port,
			["token"] = token,
			["heartbeat_utc"] = DateTime.UtcNow.ToString( "O" )
		};

		var tmp = registryFile + ".tmp";
		using ( var file = new FileStream( tmp, FileMode.Create, FileAccess.Write, FileShare.None ) )
		{
			SetUnixMode( tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite );
			using var writer = new StreamWriter( file );
			writer.Write( record.ToJsonString( CompactJson ) );
		}

		File.Move( tmp, registryFile, true );
		SetUnixMode( registryFile, UnixFileMode.UserRead | UnixFileMode.UserWrite );
	}

	static bool TokenEquals( string candidate, string expected )
	{
		if ( string.IsNullOrEmpty( candidate ) || string.IsNullOrEmpty( expected ) )
			return false;

		var candidateBytes = Encoding.UTF8.GetBytes( candidate );
		var expectedBytes = Encoding.UTF8.GetBytes( expected );
		return candidateBytes.Length == expectedBytes.Length && CryptographicOperations.FixedTimeEquals( candidateBytes, expectedBytes );
	}

	static ImageDimensions? TryReadPngDimensions( string path )
	{
		try
		{
			var bytes = File.ReadAllBytes( path );
			if ( bytes.Length < 24 ) return null;
			if ( bytes[0] != 0x89 || bytes[1] != 0x50 || bytes[2] != 0x4e || bytes[3] != 0x47 ) return null;
			if ( bytes[4] != 0x0d || bytes[5] != 0x0a || bytes[6] != 0x1a || bytes[7] != 0x0a ) return null;
			return new ImageDimensions( ReadInt32BigEndian( bytes, 16 ), ReadInt32BigEndian( bytes, 20 ) );
		}
		catch
		{
			return null;
		}
	}

	static int ReadInt32BigEndian( byte[] bytes, int offset )
	{
		return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
	}

	static string RegistryDirectory()
	{
		var xdg = Environment.GetEnvironmentVariable( "XDG_RUNTIME_DIR" );
		if ( !string.IsNullOrWhiteSpace( xdg ) )
			return Path.Combine( xdg, "sbox-mcp", "instances" );

		return Path.Combine( Path.GetTempPath(), $"sbox-mcp-{CurrentUid()}", "instances" );
	}

	static string CurrentUid()
	{
		if ( OperatingSystem.IsWindows() ) return Environment.UserName;

		try
		{
			return getuid().ToString( CultureInfo.InvariantCulture );
		}
		catch
		{
			return Environment.UserName;
		}
	}

	static void SetUnixMode( string path, UnixFileMode mode )
	{
		if ( OperatingSystem.IsWindows() ) return;

		try
		{
			File.SetUnixFileMode( path, mode );
		}
		catch
		{
		}
	}

	[DllImport( "libc", EntryPoint = "geteuid" )]
	static extern uint getuid();

	readonly record struct ConsoleEntry( long Sequence, string Level, string Logger, string Message, DateTime TimeUtc, string ExceptionType, string Stack )
	{
		public JsonObject ToJson()
		{
			return new JsonObject
			{
				["sequence"] = Sequence,
				["level"] = Level,
				["logger"] = Logger,
				["message"] = Message,
				["time_utc"] = TimeUtc.ToString( "O" ),
				["exception_type"] = ExceptionType,
				["stack"] = Stack
			};
		}
	}

	readonly record struct ImageDimensions( int Width, int Height );
}

internal static class SboxMcpMainThread
{
	public static Task<T> InvokeAsync<T>( Func<T> func, TimeSpan timeout )
	{
		var tcs = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		var completed = 0;

		_ = Task.Delay( timeout ).ContinueWith( _ =>
		{
			if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
				tcs.TrySetException( new SboxMcpBridgeException( "operation_timeout", "Timed out waiting for editor main thread" ) );
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );

		MainThread.Queue( () =>
		{
			if ( Volatile.Read( ref completed ) != 0 ) return;

			try
			{
				var result = func();
				if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
					tcs.TrySetResult( result );
			}
			catch ( Exception e )
			{
				if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
					tcs.TrySetException( e );
			}
		} );

		return tcs.Task;
	}

	public static Task<T> InvokeAsync<T>( Func<Task<T>> func, TimeSpan timeout )
	{
		var tcs = new TaskCompletionSource<T>( TaskCreationOptions.RunContinuationsAsynchronously );
		var completed = 0;

		_ = Task.Delay( timeout ).ContinueWith( _ =>
		{
			if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
				tcs.TrySetException( new SboxMcpBridgeException( "operation_timeout", "Timed out waiting for editor main thread" ) );
		}, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default );

		MainThread.Queue( async () =>
		{
			if ( Volatile.Read( ref completed ) != 0 ) return;

			try
			{
				var result = await func();
				if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
					tcs.TrySetResult( result );
			}
			catch ( Exception e )
			{
				if ( Interlocked.CompareExchange( ref completed, 1, 0 ) == 0 )
					tcs.TrySetException( e );
			}
		} );

		return tcs.Task;
	}
}

internal sealed class SboxMcpBridgeException : Exception
{
	public string Code { get; }
	public JsonObject Details { get; }

	public SboxMcpBridgeException( string code, string message, JsonObject details = null ) : base( message )
	{
		Code = code;
		Details = details ?? new JsonObject();
	}
}
