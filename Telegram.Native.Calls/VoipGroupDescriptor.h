#pragma once

#include "VoipGroupDescriptor.g.h"

namespace winrt::Telegram::Native::Calls::implementation
{
    struct VoipGroupDescriptor : VoipGroupDescriptorT<VoipGroupDescriptor>
    {
        VoipGroupDescriptor() = default;

        hstring AudioInputId();
        void AudioInputId(hstring value);

        hstring AudioOutputId();
        void AudioOutputId(hstring value);

        VoipVideoContentType VideoContentType();
        void VideoContentType(VoipVideoContentType value);

        VoipCaptureBase VideoCapture();
        void VideoCapture(VoipCaptureBase value);

        bool IsConference();
        void IsConference(bool value);

        bool IsNoiseSuppressionEnabled();
        void IsNoiseSuppressionEnabled(bool value);

        bool IsReferenceImpl();
        void IsReferenceImpl(bool value);

        bool IsStream();
        void IsStream(bool value);

        int64_t AudioProcessId();
        void AudioProcessId(int64_t value);

    private:
        hstring m_audioInputId{ L"" };
        hstring m_audioOutputId{ L"" };
        VoipVideoContentType m_videoContentType{ VoipVideoContentType::Generic };
        VoipCaptureBase m_videoCapture{ nullptr };
        bool m_isConference{ false };
        bool m_isNoiseSuppressionEnabled{ true };
        bool m_isReferenceImpl{ false };
        bool m_isStream{ false };
        int64_t m_audioProcessId{ 0 };
    };
}

namespace winrt::Telegram::Native::Calls::factory_implementation
{
    struct VoipGroupDescriptor : VoipGroupDescriptorT<VoipGroupDescriptor, implementation::VoipGroupDescriptor>
    {
    };
}
