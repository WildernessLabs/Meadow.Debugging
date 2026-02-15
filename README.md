# Meadow.Debugging

The shared Debug Adapter Protocol (DAP) implementation used by Visual Studio, Visual Studio Code, and Rider for Meadow debugging. This repository contains everything needed to deploy and debug Meadow applications, regardless of which IDE you're using.

## Why This Exists

Before DAP, each IDE had its own debugging implementation. VSCode did debugging one way, Visual Studio did it differently, and Rider had yet another approach. This meant:

- Bugs fixed in one IDE weren't fixed in the others
- Adding new debugging features required updating three separate codebases
- The team spent more time maintaining IDE integrations than improving the debugging experience

By standardizing on DAP, we now maintain a single debugging implementation. When a bug is fixed, it's fixed for all three IDEs simultaneously. When a feature is added, all users get it at the same time. This is a significant win for consistency and maintainability.

## Architecture

The debugging system is split into three main projects:

### Meadow.Debugging.Core

Contains the core functionality for communicating with Meadow devices and managing deployment. This includes:

- Device connection management
- Binary deployment and file transfer
- Runtime state management
- Device communication protocols

This layer handles the "hardware side" of debugging. It doesn't know anything about IDEs or debug protocols. It just knows how to talk to a Meadow device.

### Meadow.Debugging.DAP

Implements the Debug Adapter Protocol server and the Meadow-specific debug session. This is where most of the "what does a debug session do?" logic lives:

- MeadowDebugSession - The main DAP command handler
- SoftDebuggerAdapter - Wraps the Mono.Debugging soft debugger and exposes it as IDebugger
- Protocol classes - DAP request and response types
- Event emitters - Convert internal events to DAP events

The DAP layer is IDE-agnostic. It implements the protocol that any IDE can speak. It doesn't care whether the client is VSCode, Visual Studio, or Rider. It just receives DAP commands, processes them, and sends back events.

### Meadow.Debugging.Host

The console application (meadow-debugging.exe) that IDEs actually launch. This is the bridge between the IDE's debug protocol handler and our DAP implementation:

- Reads DAP requests from stdin
- Parses JSON protocol messages
- Invokes MeadowDebugSession methods
- Writes DAP responses to stdout

When an IDE needs to debug a Meadow app, it launches this executable as a subprocess and communicates with it through stdin/stdout using JSON messages.

## How Debugging Works

When a user clicks the debug button in their IDE, here's the flow:

1. IDE extension prepares launch configuration (project path, serial port, etc.)
2. IDE launches meadow-debugging.exe as a subprocess
3. IDE sends a DAP initialize request
4. IDE sends a DAP launch request with the configuration
5. meadow-debugging.exe (our Host) parses the request and calls MeadowDebugSession.Launch()
6. MeadowDebugSession creates a MeadowDeployer to handle deployment to the device
7. Once deployed, it connects to the device's Mono debugger on the debug port
8. IDE receives debugging events (stopped, breakpoint hit, thread started, etc.)
9. IDE shows these events in the debug UI (variables, call stack, etc.)
10. User sets breakpoints, steps through code, inspects variables
11. When user clicks stop, IDE sends a DAP disconnect request
12. meadow-debugging.exe calls MeadowDebugSession.Disconnect()
13. Disconnect resumes the device, closes connections, and returns immediately
14. meadow-debugging.exe exits cleanly

The key insight is that the IDE doesn't talk to the device. The IDE talks to our adapter, which talks to the device.

## Important Implementation Details

### The Disconnect Pattern

One thing to be aware of when implementing your own IDE extension: the Disconnect() method must return immediately. Don't wait for cleanup to finish in the Disconnect() handler.

Here's why: IDEs have their own timeouts for how long they'll wait for a disconnect response. If your cleanup takes longer than that timeout (usually 3-5 seconds), the IDE will forcibly kill your adapter process mid-operation. This can leave the device in an unstable state.

The correct pattern is:

```csharp
public override async Task Disconnect(Response response, dynamic arguments)
{
    // Send response immediately to unblock the IDE
    SendResponse(response);
    
    // Then do cleanup asynchronously in background
    _ = Task.Run(async () => {
        try {
            // All your cleanup here - resume device, close connections, etc.
        }
        catch (Exception ex) {
            Log($"Cleanup error: {ex.Message}");
        }
    });
}
```

This ensures the IDE gets its response fast and can shut down cleanly, while cleanup continues in the background. The device still gets resumed properly, but it happens after the IDE is no longer waiting.

### Session Lifecycle

A debug session goes through these stages:

1. Initialize - IDE and adapter exchange capabilities
2. Launch - Deployment and debug connection established
3. Running - IDE receives events, user can pause/step
4. Stopped - Breakpoint hit, step completed, exception thrown
5. Disconnect - Cleanup and shutdown

The Launch and Disconnect stages are the most complex. Launch must set up the device connection without blocking the IDE. Disconnect must clean up without blocking the IDE.

### Events

The adapter emits events back to the IDE through the DAP protocol. The main ones are:

- Stopped - A thread hit a breakpoint or has stepped
- Started - A new thread has started on the device
- Exited - A thread has exited
- Terminated - The entire debugging session has ended
- Output - Text output from the device

The IDE uses these events to update its UI. When a user sees a debugger stopped at a breakpoint, that's an event coming from this adapter.

## Building

To build the solution:

```
dotnet build Meadow.Debugging.sln
```

The Host project produces meadow-debugging.exe, which is what gets bundled into each IDE extension/plugin.

## For IDE Extension Developers

If you're building a new IDE extension for Meadow debugging, here's what you need to know:

### Step 1: Understand Your IDE's Debug Protocol

Every IDE has a way to integrate with external debuggers. VSCode uses the Debug Adapter Protocol. Visual Studio has its own Debug Engine interface. Rider uses the IntelliJ debug system. Familiarize yourself with how your IDE launches and communicates with debuggers.

### Step 2: Prepare Launch Configuration

Your IDE extension needs to gather information about what to debug:

- Project path
- Target device serial port
- Build configuration (Debug/Release)
- Output path where binaries are built
- Debug port for the Mono soft debugger

Create an MSBuild property file or similar that the adapter can read. The adapter needs to know where these build outputs are.

### Step 3: Launch the Adapter

Launch meadow-debugging.exe as a subprocess with appropriate arguments. The exact invocation depends on your IDE's API, but it should be something like:

```
meadow-debugging.exe --trace
```

The --trace flag enables diagnostic tracing. You can add --log-file if you need file logging for troubleshooting during development.

### Step 4: Communicate via DAP

Send DAP protocol messages to the adapter's stdin and read responses from stdout. The messages are newline-delimited JSON. Each message has a sequence number and type.

Example initialize request:

```json
{"seq":1,"type":"request","command":"initialize","arguments":{"clientID":"myide","clientName":"My IDE"}}
```

The adapter will respond with a response message and eventually an initialized event.

### Step 5: Implement IDE-Specific UI

Handle the events coming back from the adapter and update your IDE's debug UI. This is where you hook into your IDE's debugger UI framework (breakpoints, call stacks, variables, etc.).

### Step 6: Handle Disconnect Properly

When the user stops debugging, don't wait for all cleanup to finish before returning control to the IDE. Send the response immediately and let cleanup happen in the background. This is critical for a good user experience.

## Extending the Adapter

If you need to add new functionality to the debugging system:

1. Core device communication changes go in Meadow.Debugging.Core
2. DAP protocol or session changes go in Meadow.Debugging.DAP
3. Host application changes go in Meadow.Debugging.Host

The adapter is designed to be IDE-agnostic. If your change is specifically for one IDE's UI, that belongs in the IDE extension, not here.

## Testing

The best way to test is to actually use the debugger with each IDE:

- VSCode extension testing uses the sandboxed VSCode instance
- VS2022 testing uses the experimental Hive registry
- Rider testing uses the sandboxed Rider instance

For unit testing the core deployment and debugging logic, add tests to the Core project.

## Common Issues

### Device Left in Broken State After Debug

This usually means the Disconnect() cleanup didn't complete. Check:

1. Is Disconnect() being called at all? Add logging to verify.
2. Is the device being resumed with Continue()? This is critical.
3. Is cleanup happening asynchronously without blocking? Don't wait in Disconnect().

### Timeout Errors When Starting Debug

The IDE's timeout waiting for the adapter to respond during launch. This could be:

1. Deployment taking too long - optimize file transfer in Meadow.Debugging.Core
2. Device connection taking too long - check network/serial connection quality
3. Mono debugger connection taking too long - verify the debug port is correct

### Breakpoints Not Working

1. Is the MSBuild property file being found and parsed correctly?
2. Are the binary files actually deployed to the device?
3. Is the Mono soft debugger connected on the right debug port?

Add logging to MeadowDebugSession and SoftDebuggerAdapter to trace the problem.

## License

Released under the Apache 2 license.

## Contributing

When contributing to the adapter:

- Maintain IDE-agnostic code in the core projects
- Add comprehensive logging for troubleshooting
- Test with all three IDEs before submitting changes
- Remember: a fix here benefits users of all three IDEs
