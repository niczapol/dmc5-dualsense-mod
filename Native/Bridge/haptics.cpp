#include "haptics.hpp"
#include "core.hpp"

#include <Windows.h>
#include <initguid.h>
#include <audioclient.h>
#include <endpointvolume.h>
#include <functiondiscoverykeys_devpkey.h>
#include <mmdeviceapi.h>
#include <propvarutil.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstring>
#include <fstream>
#include <mutex>
#include <numbers>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

namespace dmc5ds {
namespace {

constexpr int kSampleRate = 48'000;
constexpr int kChannels = 4;
const PROPERTYKEY kControllerDeviceId{
    {0xb3f8fa53, 0x0004, 0x438e, {0x90, 0x03, 0x51, 0xa4, 0x6e, 0x13, 0x9b, 0xfc}}, 2};
const PROPERTYKEY kDeviceInterfaceKey{
    {0x233164c8, 0x1b2c, 0x4c7d, {0xbc, 0x68, 0xb6, 0x71, 0x68, 0x7a, 0x25, 0x67}}, 1};
using Clock = std::chrono::steady_clock;

template <typename T>
void release(T*& value) {
    if (value != nullptr) value->Release();
    value = nullptr;
}

std::wstring wide(const std::string& value) {
    if (value.empty()) return {};
    const int size = MultiByteToWideChar(CP_UTF8, 0, value.data(),
                                         static_cast<int>(value.size()), nullptr, 0);
    std::wstring result(static_cast<std::size_t>(size), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        result.data(), size);
    return result;
}

std::string utf8(const std::wstring& value) {
    if (value.empty()) return {};
    const int size = WideCharToMultiByte(CP_UTF8, 0, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    std::string result(static_cast<std::size_t>(size), '\0');
    WideCharToMultiByte(CP_UTF8, 0, value.data(), static_cast<int>(value.size()),
                        result.data(), size, nullptr, nullptr);
    return result;
}

std::string lower_ascii(std::string value) {
    std::transform(value.begin(), value.end(), value.begin(), [](unsigned char character) {
        return static_cast<char>(std::tolower(character));
    });
    return value;
}

std::uint16_t read_u16(std::istream& stream) {
    std::array<std::uint8_t, 2> bytes{};
    stream.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
    return static_cast<std::uint16_t>(bytes[0] | (bytes[1] << 8));
}

std::uint32_t read_u32(std::istream& stream) {
    std::array<std::uint8_t, 4> bytes{};
    stream.read(reinterpret_cast<char*>(bytes.data()), bytes.size());
    return static_cast<std::uint32_t>(bytes[0] | (bytes[1] << 8) |
        (bytes[2] << 16) | (bytes[3] << 24));
}

std::string normalize_event(std::string name) {
    for (auto& character : name) {
        if (character == '-' || character == ' ') character = '_';
        else character = static_cast<char>(std::tolower(static_cast<unsigned char>(character)));
    }
    static const std::unordered_map<std::string, std::string> aliases{
        {"blue_rose_shot", "bluerose_shot_shell"},
        {"bluerose_shot", "bluerose_shot_shell"},
        {"dante_coyote_shot", "coyote_shot_shell"},
        {"dante_evony_shot", "evony_shot_shell"},
        {"dante_ebony_shot", "evony_shot_shell"},
        {"dante_ivory_shot", "ivory_shot_shell"},
        {"judgement_cut", "jigenzan_shot_shell"},
        {"jigenzan_shot", "jigenzan_shot_shell"},
        {"judgement_cut_jr", "jr_jigenzan_shot_shell"},
        {"jr_jigenzan_shot", "jr_jigenzan_shot_shell"},
        {"beowulf_pre", "beo_sp_pre"}, {"beowulf_impact", "beo_sp_impact"},
        {"mirage_loop", "mirage_sp_loop"}, {"mirage_end", "mirage_sp_end"},
        {"yamato_return", "yamato_zetsu_return"},
        {"judgement_cut_end", "yamato_zetsu_return"},
        {"yamato_noutou", "yamato_zetsu_noutou"},
        {"stop", "stop_all"}, {"stopall", "stop_all"}
    };
    const auto found = aliases.find(name);
    return found == aliases.end() ? name : found->second;
}

} // namespace

struct HapticEngine::Impl {
    struct Voice {
        int remaining{};
        int total{};
        float low_amplitude{};
        float high_amplitude{};
        double low_phase{};
        double high_phase{};
        float low_frequency{};
        float high_frequency{};
    };

    struct Sample {
        int index{};
        std::string key;
        std::string file;
        std::vector<float> interleaved;
        int channels{};
        float gain{};
        double playback_rate{1.0};
        int delay_frames{};
        bool loop{};
    };

    struct SampleVoice {
        std::string key;
        const Sample* sample{};
        double position{};
        float gain{};
    };

    mutable std::mutex gate;
    float strength{};
    std::string status{"disabled"};
    std::unordered_map<std::string, Sample> samples;
    std::vector<Voice> voices;
    std::vector<SampleVoice> sample_voices;

    RumbleRuntime rumble;
    Clock::time_point advanced_haptics_until{};

    IMMDevice* device{};
    IAudioClient* audio_client{};
    IAudioRenderClient* render_client{};
    IAudioEndpointVolume* endpoint_volume{};
    HANDLE audio_event{};
    HANDLE stop_event{};
    UINT32 buffer_frames{};
    std::thread render_thread;
    std::atomic<bool> is_started{};
    float previous_volume{};
    BOOL previous_mute{};
    float managed_volume{};
    bool volume_managed{};
    std::uint64_t rendered_frames{};
    std::uint64_t non_zero_frames{};
    std::uint64_t limited_frames{};
    float render_peak{};

    explicit Impl(float value)
        : strength(std::clamp(value, 0.0F, 1.0F)), rumble(strength) {}

    ~Impl() { stop_audio(); }

    void set_status(std::string value) {
        std::scoped_lock lock(gate);
        status = std::move(value);
    }

    static bool read_wave(const std::filesystem::path& path, Sample& sample) {
        std::ifstream stream(path, std::ios::binary);
        char tag[4]{};
        stream.read(tag, 4);
        if (!stream || std::memcmp(tag, "RIFF", 4) != 0) return false;
        (void)read_u32(stream);
        stream.read(tag, 4);
        if (std::memcmp(tag, "WAVE", 4) != 0) return false;

        std::uint16_t format{}, channels{}, bits{};
        std::uint32_t rate{};
        std::vector<std::uint8_t> data;
        while (stream.read(tag, 4)) {
            const auto size = read_u32(stream);
            if (std::memcmp(tag, "fmt ", 4) == 0) {
                format = read_u16(stream);
                channels = read_u16(stream);
                rate = read_u32(stream);
                (void)read_u32(stream);
                (void)read_u16(stream);
                bits = read_u16(stream);
                if (size > 16) stream.seekg(size - 16, std::ios::cur);
            } else if (std::memcmp(tag, "data", 4) == 0) {
                data.resize(size);
                stream.read(reinterpret_cast<char*>(data.data()), size);
            } else {
                stream.seekg(size, std::ios::cur);
            }
            if ((size & 1U) != 0) stream.seekg(1, std::ios::cur);
        }
        if (format != WAVE_FORMAT_PCM || rate != kSampleRate || bits != 16 ||
            channels < 1 || channels > 2 || data.empty() || (data.size() & 1U) != 0)
            return false;
        sample.channels = channels;
        sample.interleaved.resize(data.size() / 2);
        for (std::size_t index = 0; index < sample.interleaved.size(); ++index) {
            const auto raw = static_cast<std::uint16_t>(data[index * 2] |
                                                        (data[index * 2 + 1] << 8));
            sample.interleaved[index] = static_cast<std::int16_t>(raw) / 32768.0F;
        }
        return true;
    }

    bool load_samples(const std::filesystem::path& directory) {
        struct Spec {
            int index; const char* key; const wchar_t* file; float gain_db;
            float pitch_cents; float delay; bool loop;
        };
        static constexpr std::array specs{
            Spec{0, "coyote_shot_shell", L"87828053.wav", 3, 0, 0, false},
            Spec{1, "bluerose_shot_shell", L"683314104.wav", 0, 0, 0, false},
            Spec{2, "jr_jigenzan_shot_shell", L"297926011.wav", 5, 0, 0, false},
            Spec{3, "evony_shot_shell", L"511441928.wav", 2, 0, 0, false},
            Spec{4, "ivory_shot_shell", L"1040252522.wav", 2, 0, 0, false},
            Spec{5, "jigenzan_shot_shell", L"193630586.wav", -1, 300, 0, false},
            Spec{6, "beo_sp_impact", L"752139616.wav", 8, 0, .1F, false},
            Spec{7, "mirage_sp_loop", L"310261087.wav", 5, 0, 0, true},
            Spec{8, "mirage_sp_end", L"748704802.wav", 6, 0, 0, false},
            Spec{9, "beo_sp_pre", L"317387691.wav", 0, -250, .3F, false},
            Spec{10, "yamato_zetsu_return", L"564764444.wav", -96, 0, 0, false},
            Spec{11, "yamato_zetsu_noutou", L"726668428.wav", 1, 0, 0, false}
        };
        samples.clear();
        for (const auto& spec : specs) {
            Sample sample;
            sample.index = spec.index;
            sample.key = spec.key;
            sample.file = utf8(spec.file);
            sample.gain = std::pow(10.0F, spec.gain_db / 20.0F);
            sample.playback_rate = std::pow(2.0, spec.pitch_cents / 1200.0);
            sample.delay_frames = static_cast<int>(std::nearbyint(spec.delay * kSampleRate));
            sample.loop = spec.loop;
            if (!read_wave(directory / spec.file, sample)) {
                samples.clear();
                status = "invalid or missing haptic WAV: " + sample.file;
                return false;
            }
            samples.emplace(sample.key, std::move(sample));
        }
        return samples.size() == specs.size();
    }

    static std::wstring string_property(IPropertyStore* properties,
                                        REFPROPERTYKEY key) {
        if (properties == nullptr) return {};
        PROPVARIANT value;
        PropVariantInit(&value);
        std::wstring result;
        if (SUCCEEDED(properties->GetValue(key, &value)) &&
            value.vt == VT_LPWSTR && value.pwszVal != nullptr)
            result = value.pwszVal;
        PropVariantClear(&value);
        return result;
    }

    static int channel_count(IMMDevice* candidate) {
        IAudioClient* client{};
        WAVEFORMATEX* format{};
        int channels{};
        if (candidate != nullptr &&
            SUCCEEDED(candidate->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                          reinterpret_cast<void**>(&client))) &&
            SUCCEEDED(client->GetMixFormat(&format)) && format != nullptr)
            channels = format->nChannels;
        if (format != nullptr) CoTaskMemFree(format);
        release(client);
        return channels;
    }

    bool find_device(const std::wstring& fragment) {
        IMMDeviceEnumerator* enumerator{};
        IMMDeviceCollection* collection{};
        HRESULT result = CoCreateInstance(CLSID_MMDeviceEnumerator, nullptr, CLSCTX_ALL,
                                           IID_PPV_ARGS(&enumerator));
        if (FAILED(result)) return false;
        result = enumerator->EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, &collection);
        if (FAILED(result)) {
            release(enumerator);
            return false;
        }
        UINT count{};
        collection->GetCount(&count);
        int best_score{};
        for (UINT index = 0; index < count; ++index) {
            IMMDevice* candidate{};
            IPropertyStore* properties{};
            if (SUCCEEDED(collection->Item(index, &candidate)) &&
                SUCCEEDED(candidate->OpenPropertyStore(STGM_READ, &properties))) {
                const auto name = string_property(properties, PKEY_Device_FriendlyName);
                const auto controller_id = string_property(
                    properties, kControllerDeviceId);
                const auto interface_key = string_property(
                    properties, kDeviceInterfaceKey);
                const auto match = classify_dualsense_audio_endpoint(
                    name, fragment, controller_id, interface_key,
                    channel_count(candidate));
                if (match.score > best_score) {
                    release(device);
                    device = candidate;
                    device->AddRef();
                    best_score = match.score;
                    status = utf8(name.empty() ? L"renamed DualSense audio endpoint" : name) +
                             "; " + std::string(match.reason);
                }
            }
            release(properties);
            release(candidate);
        }
        release(collection);
        release(enumerator);
        return device != nullptr;
    }

    bool configure_volume(bool ensure_audible, float volume) {
        if (FAILED(device->Activate(__uuidof(IAudioEndpointVolume), CLSCTX_ALL, nullptr,
                                    reinterpret_cast<void**>(&endpoint_volume))))
            return false;
        endpoint_volume->GetMasterVolumeLevelScalar(&previous_volume);
        endpoint_volume->GetMute(&previous_mute);
        if (ensure_audible) {
            managed_volume = std::clamp(volume, 0.05F, 1.0F);
            endpoint_volume->SetMute(FALSE, nullptr);
            endpoint_volume->SetMasterVolumeLevelScalar(managed_volume, nullptr);
            volume_managed = true;
        }
        return true;
    }

    void restore_volume() {
        if (!volume_managed || endpoint_volume == nullptr) return;
        float current{};
        BOOL mute{};
        if (SUCCEEDED(endpoint_volume->GetMasterVolumeLevelScalar(&current)) &&
            SUCCEEDED(endpoint_volume->GetMute(&mute)) && !mute &&
            std::abs(current - managed_volume) < 0.01F) {
            endpoint_volume->SetMasterVolumeLevelScalar(previous_volume, nullptr);
            endpoint_volume->SetMute(previous_mute, nullptr);
        }
        volume_managed = false;
    }

    bool initialize_audio(bool ensure_audible, float volume) {
        if (!configure_volume(ensure_audible, volume)) return false;
        HRESULT result = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr,
                                           reinterpret_cast<void**>(&audio_client));
        if (FAILED(result)) return false;

        WAVEFORMATEXTENSIBLE format{};
        format.Format.wFormatTag = WAVE_FORMAT_EXTENSIBLE;
        format.Format.nChannels = kChannels;
        format.Format.nSamplesPerSec = kSampleRate;
        format.Format.wBitsPerSample = 16;
        format.Format.nBlockAlign = kChannels * sizeof(std::int16_t);
        format.Format.nAvgBytesPerSec = kSampleRate * format.Format.nBlockAlign;
        format.Format.cbSize = sizeof(WAVEFORMATEXTENSIBLE) - sizeof(WAVEFORMATEX);
        format.Samples.wValidBitsPerSample = 16;
        format.dwChannelMask = SPEAKER_FRONT_LEFT | SPEAKER_FRONT_RIGHT |
                               SPEAKER_BACK_LEFT | SPEAKER_BACK_RIGHT;
        format.SubFormat = GUID{0x00000001, 0x0000, 0x0010,
                                {0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71}};
        constexpr DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK |
                                AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM |
                                AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        result = audio_client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 200'000, 0,
                                           &format.Format, nullptr);
        if (FAILED(result)) return false;
        if (FAILED(audio_client->GetBufferSize(&buffer_frames))) return false;
        audio_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        if (audio_event == nullptr || stop_event == nullptr) return false;
        if (FAILED(audio_client->SetEventHandle(audio_event))) return false;
        if (FAILED(audio_client->GetService(__uuidof(IAudioRenderClient),
                                            reinterpret_cast<void**>(&render_client))))
            return false;
        BYTE* initial{};
        if (SUCCEEDED(render_client->GetBuffer(buffer_frames, &initial)))
            render_client->ReleaseBuffer(buffer_frames, AUDCLNT_BUFFERFLAGS_SILENT);
        if (FAILED(audio_client->Start())) return false;
        is_started.store(true, std::memory_order_release);
        render_thread = std::thread([this] { render_loop(); });
        return true;
    }

    void render_loop() {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        HANDLE events[]{stop_event, audio_event};
        while (WaitForMultipleObjects(2, events, FALSE, INFINITE) == WAIT_OBJECT_0 + 1) {
            UINT32 padding{};
            if (FAILED(audio_client->GetCurrentPadding(&padding)) || padding >= buffer_frames)
                continue;
            const UINT32 frames = buffer_frames - padding;
            BYTE* data{};
            if (FAILED(render_client->GetBuffer(frames, &data))) continue;
            render(reinterpret_cast<std::int16_t*>(data), frames);
            if (FAILED(render_client->ReleaseBuffer(frames, 0))) break;
        }
        CoUninitialize();
    }

    static std::int16_t to_int16(double value) {
        return static_cast<std::int16_t>(std::clamp(
            static_cast<int>(std::nearbyint(value * 32767.0)), -32768, 32767));
    }

    void render(std::int16_t* output, UINT32 frames) {
        std::scoped_lock lock(gate);
        const auto now = Clock::now();
        const auto priority_until = now + std::chrono::duration_cast<Clock::duration>(
            std::chrono::duration<double>(static_cast<double>(frames) / kSampleRate)) +
            std::chrono::milliseconds(50);
        for (UINT32 frame = 0; frame < frames; ++frame) {
            double left{}, right{};
            bool advanced_frame{};

            for (std::size_t index = voices.size(); index-- > 0;) {
                auto& voice = voices[index];
                const double progress = 1.0 - static_cast<double>(voice.remaining) / voice.total;
                const double envelope = std::pow(std::max(0.0, 1.0 - progress), 1.7);
                voice.low_phase += 2.0 * std::numbers::pi * voice.low_frequency / kSampleRate;
                voice.high_phase += 2.0 * std::numbers::pi * voice.high_frequency / kSampleRate;
                const double low = std::sin(voice.low_phase) * voice.low_amplitude;
                const double high = std::sin(voice.high_phase) * voice.high_amplitude;
                const double add_left = (low + high * .62) * envelope * .64;
                const double add_right = (low * .88 + high * .78) * envelope * .64;
                left += add_left;
                right += add_right;
                advanced_frame = advanced_frame || std::abs(add_left) > .0001 ||
                                 std::abs(add_right) > .0001;
                if (--voice.remaining <= 0) voices.erase(voices.begin() + index);
            }

            for (std::size_t index = sample_voices.size(); index-- > 0;) {
                auto& voice = sample_voices[index];
                if (voice.position < 0) {
                    voice.position += 1;
                    continue;
                }
                const auto& sample = *voice.sample;
                const int count = static_cast<int>(sample.interleaved.size()) / sample.channels;
                if (voice.position >= count) {
                    if (!sample.loop) {
                        sample_voices.erase(sample_voices.begin() + index);
                        continue;
                    }
                    voice.position = std::fmod(voice.position, count);
                }
                const int frame0 = std::clamp(static_cast<int>(voice.position), 0, count - 1);
                const int frame1 = sample.loop ? (frame0 + 1) % count
                                               : std::min(frame0 + 1, count - 1);
                const double amount = voice.position - frame0;
                const auto interpolate = [&](int channel) {
                    const float a = sample.interleaved[frame0 * sample.channels + channel];
                    const float b = sample.interleaved[frame1 * sample.channels + channel];
                    return a + (b - a) * amount;
                };
                const double source_left = interpolate(0);
                const double source_right = sample.channels == 1 ? source_left : interpolate(1);
                const double add_left = source_left * voice.gain;
                const double add_right = source_right * voice.gain;
                left += add_left;
                right += add_right;
                advanced_frame = advanced_frame || std::abs(add_left) > .0001 ||
                                 std::abs(add_right) > .0001;
                voice.position += sample.playback_rate;
            }

            if (advanced_frame)
                advanced_haptics_until = priority_until;
            if (std::abs(left) > .90 || std::abs(right) > .90) ++limited_frames;
            const auto left_sample = to_int16(soft_limit_haptic(left));
            const auto right_sample = to_int16(soft_limit_haptic(right));
            output[frame * 4 + 0] = 0;
            output[frame * 4 + 1] = 0;
            output[frame * 4 + 2] = left_sample;
            output[frame * 4 + 3] = right_sample;
            ++rendered_frames;
            if (left_sample != 0 || right_sample != 0) ++non_zero_frames;
            render_peak = std::max(render_peak, std::max(std::abs(left_sample / 32768.0F),
                                                          std::abs(right_sample / 32768.0F)));
        }
    }

    void stop_audio() {
        if (stop_event != nullptr) SetEvent(stop_event);
        if (render_thread.joinable()) render_thread.join();
        if (audio_client != nullptr) audio_client->Stop();
        is_started.store(false, std::memory_order_release);
        restore_volume();
        release(render_client);
        release(audio_client);
        release(endpoint_volume);
        release(device);
        if (audio_event != nullptr) CloseHandle(audio_event);
        if (stop_event != nullptr) CloseHandle(stop_event);
        audio_event = nullptr;
        stop_event = nullptr;
    }
};

HapticEngine::HapticEngine(float strength) : impl_(std::make_unique<Impl>(strength)) {}
HapticEngine::~HapticEngine() = default;

bool HapticEngine::start(const std::string& fragment, bool ensure_audible, float volume,
                         const std::filesystem::path& sample_directory) {
    if (impl_->is_started.load(std::memory_order_acquire)) return true;
    std::scoped_lock lock(impl_->gate);
    if (!impl_->load_samples(sample_directory)) return false;
    if (!impl_->find_device(wide(fragment))) {
        impl_->status = "DualSense 4-channel audio endpoint not found";
        return false;
    }
    const std::string device_name = impl_->status;
    if (!impl_->initialize_audio(ensure_audible, volume)) {
        impl_->status = device_name + "; WASAPI initialization failed";
        impl_->stop_audio();
        return false;
    }
    impl_->status = device_name + "; native WASAPI; " +
                    std::to_string(impl_->samples.size()) + "/12 original PS5 samples";
    return true;
}

bool HapticEngine::started() const { return impl_->is_started.load(std::memory_order_acquire); }
std::string HapticEngine::status() const {
    std::scoped_lock lock(impl_->gate);
    return impl_->status;
}
std::size_t HapticEngine::original_sample_count() const {
    std::scoped_lock lock(impl_->gate);
    return impl_->samples.size();
}

bool HapticEngine::play_original(const std::string& event_name) {
    const auto key = normalize_event(event_name);
    std::scoped_lock lock(impl_->gate);
    if (key == "stop_all") {
        impl_->sample_voices.clear();
        return true;
    }
    if (key == "mirage_sp_end") {
        std::erase_if(impl_->sample_voices, [](const Impl::SampleVoice& voice) {
            return voice.key == "mirage_sp_loop";
        });
    }
    const auto found = impl_->samples.find(key);
    if (found == impl_->samples.end()) return false;
    std::erase_if(impl_->sample_voices, [&](const Impl::SampleVoice& voice) {
        return voice.key == key;
    });
    impl_->sample_voices.push_back({key, &found->second,
        -static_cast<double>(found->second.delay_frames),
        found->second.gain * impl_->strength});
    return true;
}

void HapticEngine::stop_original() {
    std::scoped_lock lock(impl_->gate);
    impl_->sample_voices.clear();
}

void HapticEngine::pulse(float low, float high, float duration, float low_frequency,
                         float high_frequency) {
    low = std::clamp(low, 0.0F, 1.0F);
    high = std::clamp(high, 0.0F, 1.0F);
    duration = std::clamp(duration <= 0 ? .08F : duration, .025F, 1.5F);
    std::scoped_lock lock(impl_->gate);
    const int samples = static_cast<int>(kSampleRate * duration);
    impl_->voices.push_back({samples, samples, low * impl_->strength,
        high * impl_->strength, 0, 0, std::clamp(low_frequency, 30.0F, 220.0F),
        std::clamp(high_frequency, 40.0F, 320.0F)});
    if (impl_->voices.size() > 24)
        impl_->voices.erase(impl_->voices.begin(), impl_->voices.end() - 24);
}

void HapticEngine::rumble_pulse(float low, float high, float duration) {
    std::scoped_lock lock(impl_->gate);
    impl_->rumble.pulse(low, high, duration);
}

void HapticEngine::impact(float amount) {
    amount = std::clamp(amount, 0.0F, 1.0F);
    pulse(amount, amount * .72F, .12F);
}

void HapticEngine::from_game_pad_shake(int motor, float power, float duration) {
    std::scoped_lock lock(impl_->gate);
    if (impl_->rumble.has_recent_game_motor(std::chrono::milliseconds(120))) return;
    power = std::clamp(power, 0.0F, 1.0F);
    if (motor == 1) impl_->rumble.pulse(power, power, duration);
    else if (motor == 2) impl_->rumble.pulse(power, 0, duration);
    else if (motor == 3) impl_->rumble.pulse(0, power, duration);
}

RumbleOutput HapticEngine::rumble_output() {
    std::scoped_lock lock(impl_->gate);
    const auto now = Clock::now();
    return arbitrate_rumble(impl_->rumble.output(now),
                            now < impl_->advanced_haptics_until);
}

void HapticEngine::weapon_hit(const std::string& character, float amount) {
    amount = std::clamp(amount, .2F, 1.0F);
    const auto value = lower_ascii(character);
    if (value == "nero") pulse(.62F * amount, .86F * amount, .105F, 76, 205);
    else if (value == "dante") pulse(.90F * amount, .68F * amount, .125F, 61, 178);
    else if (value == "v") pulse(.38F * amount, .93F * amount, .115F, 96, 238);
    else if (value == "vergil") pulse(.50F * amount, 1.0F * amount, .095F, 88, 255);
    else pulse(.64F * amount, .78F * amount, .11F, 72, 200);
}

void HapticEngine::set_game_motor(int motor, float power) {
    std::scoped_lock lock(impl_->gate);
    impl_->rumble.set_game_motor(motor, power);
}

AudioRenderDiagnostic HapticEngine::take_render_diagnostic() {
    std::scoped_lock lock(impl_->gate);
    AudioRenderDiagnostic result{impl_->rendered_frames, impl_->non_zero_frames,
        impl_->limited_frames, impl_->render_peak,
        impl_->is_started.load() ? "Playing" : "Stopped"};
    impl_->rendered_frames = impl_->non_zero_frames = impl_->limited_frames = 0;
    impl_->render_peak = 0;
    return result;
}

} // namespace dmc5ds
