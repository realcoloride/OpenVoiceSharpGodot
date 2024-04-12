using Godot;
using OpenVoiceSharp;
using System.Collections.Generic;

public partial class VoiceChatManager : Node
{
	public VoiceChatInterface VoiceChatInterface = new(stereo: true, enableNoiseSuppression: false);
	public BasicMicrophoneRecorder MicrophoneRecorder = new(true);

	public int RecordBusIndex;

	public Control Control => GetParent() as Control;
	public Button HostButton => Control.GetNode("Host") as Button;
	public Button ConnectButton => Control.GetNode("Connect") as Button;
	public Label StatusLabel => Control.GetNode("Status") as Label;
	public LineEdit IPTextBox => Control.GetNode("IP") as LineEdit;
	public CheckBox NoiseSuppressionBox => Control.GetNode("NoiseSuppression") as CheckBox;
	public CheckBox ReverbBox => Control.GetNode("Reverb") as CheckBox;


	public ENetMultiplayerPeer Peer = new();
	public bool Connected;

	public void SetStatus(string text) => StatusLabel.Text = text;

	public List<(byte[], int)> QueuedData = new();

	public void CreateStreamPlayer(long id) {
		AudioStreamPlayer streamPlayer = new() {
			Name = id.ToString(),
			Stream = new AudioStreamGenerator() { 
				MixRate = 48000, 
				BufferLength = 0.02f * 60.0f // increase if needed
			},
			Autoplay = true,
			Bus = "VC" // you can change it to what you want
		};

		AddChild(streamPlayer);
		streamPlayer.Play();
	}
	public override void _Ready()
	{
		// setup multiplayer (this can also be done in gdscript!)

		// setup buttons/events
		HostButton.Pressed += () => {
			Peer.CreateServer(19994);
			Multiplayer.MultiplayerPeer = Peer;

			Connected = true;
			SetStatus("Hosting server");
			
			// create even one for yourself
			CreateStreamPlayer(Peer.GetUniqueId());
		};
		ConnectButton.Pressed += () => {
			Error status = Peer.CreateClient(IPTextBox.Text, 19994);
			if (status != Error.Ok)
			{
				SetStatus("Could not connect " + status);
				return;
			}
			
			Multiplayer.MultiplayerPeer = Peer;
			SetStatus("Connecting to server " + IPTextBox.Text + ":" + 19994);
		};
		NoiseSuppressionBox.Toggled += (toggled) => {
			VoiceChatInterface.EnableNoiseSuppression = toggled;
		};

		// create reverb effect 
		int reverbBusIdx = AudioServer.BusCount;
		AudioServer.AddBus(reverbBusIdx);
		AudioServer.AddBusEffect(reverbBusIdx, new AudioEffectReverb(), 0);
		AudioServer.SetBusName(reverbBusIdx, "VC");
		AudioServer.SetBusEffectEnabled(reverbBusIdx, 0, false);

		ReverbBox.Toggled += (toggled) => {
			AudioServer.SetBusEffectEnabled(reverbBusIdx, 0, toggled);
		};

		Multiplayer.ConnectedToServer += () => {
			Connected = true;

			int[] previousPeers = Multiplayer.GetPeers();
			for (int i = 0; i < previousPeers.Length; i++) {
				CreateStreamPlayer(previousPeers[i]);
			}

			SetStatus("Connected");
		};
		Multiplayer.ServerDisconnected += () => {
			Connected = false;
			SetStatus("Disconnected");
		};

		// create streams / playbacks
		Multiplayer.PeerConnected += (id) => {
			CreateStreamPlayer(id);
		};

		Multiplayer.PeerDisconnected += (id) => {
			if (!HasNode(id.ToString())) return;

			RemoveChild(GetNode(id.ToString()));
		};

		// microphone rec
		MicrophoneRecorder.DataAvailable += (pcmData, length) => {
			// if not connected or not talking, ignore
			if (!Connected) return;
			if (!VoiceChatInterface.IsSpeaking(pcmData)) return;

			// encode the audio data and apply noise suppression.
			// you cannot call RPC if its not the main thread, so we queue it.
			QueuedData.Add(VoiceChatInterface.SubmitAudioData(pcmData, length));
		};
		MicrophoneRecorder.StartRecording();
	}

    public override void _Process(double delta)
    {
        // check and send queue
		for (int i = 0; i < QueuedData.Count; i++) {
			(byte[] encodedData, int encodedLength) = QueuedData[i];
			Rpc("VoiceChat", encodedData, encodedLength);

			QueuedData.Remove((encodedData, encodedLength));
		}
    }

    [Rpc(MultiplayerApi.RpcMode.AnyPeer, CallLocal = false)]
	public void VoiceChat(byte[] encodedData, int encodedLength) {
		// playback
		int senderId = Multiplayer.GetRemoteSenderId();

		// decode data
		(byte[] decodedData, int decodedLength) = VoiceChatInterface.WhenDataReceived(encodedData, encodedLength);

		AudioStreamPlayer streamPlayer = GetNode(senderId.ToString()) as AudioStreamPlayer;
		var playback = streamPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;

		if (playback.GetFramesAvailable() < 0) return;

		// step 1, convert to float32
		float[] samples = new float[decodedLength / 2]; // half it
		VoiceUtilities.Convert16BitToFloat(decodedData, samples);

		// step 2, convert to vector2
		Vector2 sample;

		for (int i = 0; i < samples.Length; i += 2) {
			sample.X = samples[i];
			sample.Y = samples[i + 1];

			playback.PushFrame(sample);
		}
	}
}
