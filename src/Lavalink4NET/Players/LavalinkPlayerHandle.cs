﻿namespace Lavalink4NET.Players;

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Lavalink4NET.Clients;
using Lavalink4NET.Protocol.Requests;
using Lavalink4NET.Rest;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

internal sealed class LavalinkPlayerHandle<TPlayer, TOptions> : ILavalinkPlayerHandle, IAsyncDisposable
	where TPlayer : ILavalinkPlayer
	where TOptions : LavalinkPlayerOptions
{
	private readonly ulong _guildId;
	private readonly ILogger<TPlayer> _logger;
	private readonly IOptions<TOptions> _options;
	private readonly PlayerContext _playerContext;
	private readonly PlayerFactory<TPlayer, TOptions> _playerFactory;
	private object _value;
	private VoiceServer? _voiceServer;
	private VoiceState? _voiceState;
	private int _disposeState;

	public LavalinkPlayerHandle(
		ulong guildId,
		PlayerContext playerContext,
		PlayerFactory<TPlayer, TOptions> playerFactory,
		IOptions<TOptions> options,
		ILogger<TPlayer> logger)
	{
		ArgumentNullException.ThrowIfNull(playerContext);
		ArgumentNullException.ThrowIfNull(playerFactory);
		ArgumentNullException.ThrowIfNull(options);
		ArgumentNullException.ThrowIfNull(logger);

		_value = new TaskCompletionSource<ILavalinkPlayer>(TaskCreationOptions.RunContinuationsAsynchronously);

		_guildId = guildId;
		_playerContext = playerContext;
		_playerFactory = playerFactory;
		_options = options;
		_logger = logger;

		Interlocked.Increment(ref Diagnostics.PlayerHandles);
		Interlocked.Increment(ref Diagnostics.PendingHandles);
	}

	public ILavalinkPlayer? Player => _value as ILavalinkPlayer;

	public async ValueTask DisposeAsync()
	{
		if (Interlocked.CompareExchange(ref _disposeState, 1, 0) is not 0)
		{
			return;
		}

		if (_value is ILavalinkPlayer player)
		{
			await using var _ = player.ConfigureAwait(false);

			Interlocked.Decrement(ref Diagnostics.ActivePlayers);
		}
		else
		{
			Interlocked.Decrement(ref Diagnostics.PendingHandles);
		}

		Interlocked.Decrement(ref Diagnostics.PlayerHandles);
	}

	public ValueTask<ILavalinkPlayer> GetPlayerAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		if (_value is TaskCompletionSource<ILavalinkPlayer> taskCompletionSource)
		{
			return new ValueTask<ILavalinkPlayer>(task: taskCompletionSource.Task);
		}

		return ValueTask.FromResult<ILavalinkPlayer>(Unsafe.As<object, TPlayer>(ref Unsafe.AsRef(_value)));
	}

	public async ValueTask UpdateVoiceServerAsync(VoiceServer voiceServer, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(voiceServer);

		_voiceServer = voiceServer;

		if (_voiceState is not null)
		{
			await CompleteAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	public async ValueTask UpdateVoiceStateAsync(VoiceState voiceState, CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();
		ArgumentNullException.ThrowIfNull(voiceState);

		_voiceState = voiceState;

		if (_voiceServer is not null)
		{
			await CompleteAsync(cancellationToken).ConfigureAwait(false);
		}
	}

	private void ThrowIfDisposed()
	{
		if (_disposeState is not 0)
		{
			throw new ObjectDisposedException(objectName: nameof(LavalinkPlayerHandle<TPlayer, TOptions>));
		}
	}

	private async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		Debug.Assert(_voiceServer is not null);
		Debug.Assert(_voiceState is not null);

		if (_value is TaskCompletionSource<ILavalinkPlayer> taskCompletionSource)
		{
			var player = await CreatePlayerAsync(cancellationToken).ConfigureAwait(false);
			_value = player;

			taskCompletionSource.TrySetResult(player);

			Interlocked.Decrement(ref Diagnostics.PendingHandles);
			Interlocked.Increment(ref Diagnostics.ActivePlayers);
		}

		if (_value is ILavalinkPlayerListener playerListener)
		{
			await playerListener
				.NotifyChannelUpdateAsync(_voiceState.Value.VoiceChannelId, cancellationToken)
				.ConfigureAwait(false);
		}
	}

	private async ValueTask<TPlayer> CreatePlayerAsync(CancellationToken cancellationToken = default)
	{
		ThrowIfDisposed();
		cancellationToken.ThrowIfCancellationRequested();

		Debug.Assert(_voiceServer is not null);
		Debug.Assert(_voiceState is not null);

		var playerSession = await _playerContext.SessionProvider
			.GetSessionAsync(_guildId, cancellationToken)
			.ConfigureAwait(false);

		var playerProperties = new PlayerUpdateProperties
		{
			VoiceState = new VoiceStateProperties(
				Token: _voiceServer.Value.Token,
				Endpoint: _voiceServer.Value.Endpoint,
				SessionId: _voiceState.Value.SessionId!),
		};

		if (_options.Value.InitialTrack is not null)
		{
			var initialTrack = _options.Value.InitialTrack.Value;
			var loadOptions = _options.Value.InitialLoadOptions;

			if (initialTrack.IsPresent)
			{
				playerProperties = playerProperties with { TrackData = initialTrack.Track.ToString(), };
			}
			else
			{
				var identifier = LavalinkApiClient.BuildIdentifier(
					identifier: initialTrack.Identifier!,
					loadOptions: loadOptions);

				playerProperties = playerProperties with { Identifier = identifier, };
			}
		}

		if (_options.Value.InitialVolume is not null)
		{
			playerProperties = playerProperties with { Volume = _options.Value.InitialVolume.Value, };
		}

		var initialState = await playerSession.ApiClient
			.UpdatePlayerAsync(playerSession.SessionId, _guildId, playerProperties, cancellationToken)
			.ConfigureAwait(false);

		var label = _options.Value.Label ?? $"{typeof(TPlayer).Name}@{_guildId} on {playerSession.Label}";

		var lifecycle = _playerContext.LifecycleNotifier is null
			? EmptyPlayerLifecycle.Instance as IPlayerLifecycle
			: new PlayerLifecycle(_guildId, _playerContext.LifecycleNotifier);

		var properties = new PlayerProperties<TPlayer, TOptions>(
			Context: _playerContext,
			VoiceChannelId: _voiceState.Value.VoiceChannelId!.Value,
			InitialState: initialState,
			Label: label,
			SessionId: playerSession.SessionId,
			Lifecycle: lifecycle,
			ApiClient: playerSession.ApiClient,
			Options: _options,
			Logger: _logger);

		var player = await _playerFactory(properties, cancellationToken).ConfigureAwait(false);

		await lifecycle
			.NotifyPlayerCreatedAsync(cancellationToken)
			.ConfigureAwait(false);

		return player;
	}
}

file sealed class EmptyPlayerLifecycle : IPlayerLifecycle
{
	public static EmptyPlayerLifecycle Instance { get; } = new();

	public ValueTask DisposeAsync() => default;

	public ValueTask NotifyPlayerCreatedAsync(CancellationToken cancellationToken = default) => default;

	public ValueTask NotifyStateChangedAsync(PlayerState playerState, CancellationToken cancellationToken = default) => default;
}

file sealed class PlayerLifecycle : IPlayerLifecycle
{
	private readonly ulong _guildId;
	private readonly IPlayerLifecycleNotifier _lifecycleNotifier;

	public PlayerLifecycle(ulong guildId, IPlayerLifecycleNotifier lifecycleNotifier)
	{
		ArgumentNullException.ThrowIfNull(lifecycleNotifier);

		_guildId = guildId;
		_lifecycleNotifier = lifecycleNotifier;
	}

	public ValueTask DisposeAsync() => _lifecycleNotifier.NotifyDisposeAsync(_guildId);

	public ValueTask NotifyPlayerCreatedAsync(CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return _lifecycleNotifier.NotifyPlayerCreatedAsync(_guildId, cancellationToken);
	}

	public ValueTask NotifyStateChangedAsync(PlayerState playerState, CancellationToken cancellationToken = default)
	{
		cancellationToken.ThrowIfCancellationRequested();
		return _lifecycleNotifier.NotifyStateChangedAsync(_guildId, playerState, cancellationToken);
	}
}

file static class Diagnostics
{
	private static long _playerHandles;
	private static long _pendingHandles;
	private static long _activePlayers;

	public static ref long PlayerHandles => ref _playerHandles;

	public static ref long PendingHandles => ref _pendingHandles;

	public static ref long ActivePlayers => ref _activePlayers;

	static Diagnostics()
	{
		var meter = new Meter("Lavalink4NET");

		meter.CreateObservableGauge(
			name: "active-players",
			observeValue: static () => Volatile.Read(ref _activePlayers),
			unit: "Players",
			description: "The number of active players managed over all audio services.");

		meter.CreateObservableGauge(
			name: "player-handles",
			observeValue: static () => Volatile.Read(ref _playerHandles),
			unit: "Handles",
			description: "The number of player handles managed over all audio services.");

		meter.CreateObservableGauge(
			name: "pending-handles",
			observeValue: static () => Volatile.Read(ref _pendingHandles),
			unit: "Handles",
			description: "The number of pending player handles managed over all audio services.");
	}
}