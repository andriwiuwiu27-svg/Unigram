# Prebuilt: webrtc is built from the UnigramDev/webrtc-uwp fork by the scripts in UnigramDev/deps
# and published as release archives there. Building it from source needs depot_tools, a ~1.5 hour
# sync and ~20 GB of disk, for a result that changes a few times a year.
#
# Headers and libraries are separate archives so that a build downloads only the configuration it
# links: the two libraries are 100 MB and 156 MB compressed, against 19 MB of headers shared by all.

vcpkg_check_linkage(ONLY_STATIC_LIBRARY)

set(WEBRTC_HEADERS_SHA512 e69645001c76732a44e14a9b16f69980d04f20e8841e7853298f866c74de5ae7cd91ed5496d103fff3c5a6d5323633f8875f7408614b0c594ee23a95d1a108db)

# -uwp triplets link the winuwp build, -windows ones the desktop build of the same checkout.
if(VCPKG_CMAKE_SYSTEM_NAME STREQUAL "WindowsStore")
    set(WEBRTC_TARGET "uwp")
else()
    set(WEBRTC_TARGET "windows")
endif()

if(WEBRTC_TARGET STREQUAL "uwp" AND VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
    set(WEBRTC_RELEASE_SHA512 6a997e4d1babd3a6d25f13053e1a2532692f50abd9aa114ae1a2a54da80d9a3a3627f7c7dff52172d0c08e9aca03a783c93844efec84d6cfdc4feac3107a529e)
    set(WEBRTC_DEBUG_SHA512 2e6e21945d3be730c6a2ec884312ad301a34bed731c3a1e57d56f2d8cf2afcdc8a26e7c660a8ace91fd91ddb8034e6720cee50e06a556c2dbc5579ba9351259d)
elseif(WEBRTC_TARGET STREQUAL "uwp" AND VCPKG_TARGET_ARCHITECTURE STREQUAL "arm64")
    set(WEBRTC_RELEASE_SHA512 d398b2699e9855f1f799edf86b20cf8e307fc2b127abb81f6049c4335f6dbe01ad98d5aef8d4a93d3a296653f41301871550257a1c8dddd48b977f5243669031)
    set(WEBRTC_DEBUG_SHA512 51d12a30dca26935dc4afd158dd9e0bc8ef8fe288c80cb66164fb3c667d45088e4238afdc75630c67215eb6c0c4409b2ced3c88b0dbcc2feaf32f1e51be62f9d)
elseif(WEBRTC_TARGET STREQUAL "windows" AND VCPKG_TARGET_ARCHITECTURE STREQUAL "x64")
    set(WEBRTC_RELEASE_SHA512 38cce13160d93fd2a16bc5ce68c894f6f3f773d65b7d7845d4a21830259c37f9055441c88202ffeabf37d73b14a954b9634291af702b3f1ebbfaa23a441777ac)
    set(WEBRTC_DEBUG_SHA512 36a72ba0a1996f4e8398a2678121c3ab3f70f63b06b1beb4c8e400039c5abacb01fbc6e42c76462ce5ca2959263e5b0d4a9fa26689028dc39aaedcf57ecab82f)
elseif(WEBRTC_TARGET STREQUAL "windows" AND VCPKG_TARGET_ARCHITECTURE STREQUAL "arm64")
    set(WEBRTC_RELEASE_SHA512 b6d05918634bb6922a3892f931ee4ec2a951ef921f718c07ab3446f1ab01ac3dc4d5268c9b8b53c5b8e610942eb690218d42206af8b0ac905f1eccdd2f2e89b8)
    set(WEBRTC_DEBUG_SHA512 aaa428bf0bedadaaa3e3a07cd616a17a32eeae965dc467caf94e9a7e3b7832ef92a1a58e2e919e7ba82bc6a94c9996c3f667e71a81cf097733288d729384914a)
else()
    message(FATAL_ERROR "webrtc: no prebuilt archive for ${VCPKG_TARGET_ARCHITECTURE}-${WEBRTC_TARGET}")
endif()

set(WEBRTC_BASE_URL "https://github.com/UnigramDev/deps/releases/download/webrtc-${VERSION}-1")

vcpkg_download_distfile(HEADERS_ARCHIVE
    URLS "${WEBRTC_BASE_URL}/webrtc-${VERSION}-headers.zip"
    FILENAME "webrtc-${VERSION}-headers.zip"
    SHA512 "${WEBRTC_HEADERS_SHA512}"
)
vcpkg_extract_source_archive(HEADERS_PATH ARCHIVE "${HEADERS_ARCHIVE}" NO_REMOVE_ONE_LEVEL)
file(COPY "${HEADERS_PATH}/include" DESTINATION "${CURRENT_PACKAGES_DIR}")

# The three include roots the projects used to pass by hand -- the checkout root, abseil-cpp and
# libyuv/include -- are collapsed into this one directory by the packaging script.

if(NOT VCPKG_BUILD_TYPE STREQUAL "debug")
    vcpkg_download_distfile(RELEASE_ARCHIVE
        URLS "${WEBRTC_BASE_URL}/webrtc-${VERSION}-${VCPKG_TARGET_ARCHITECTURE}-${WEBRTC_TARGET}-release.zip"
        FILENAME "webrtc-${VERSION}-${VCPKG_TARGET_ARCHITECTURE}-${WEBRTC_TARGET}-release.zip"
        SHA512 "${WEBRTC_RELEASE_SHA512}"
    )
    vcpkg_extract_source_archive(RELEASE_PATH ARCHIVE "${RELEASE_ARCHIVE}" NO_REMOVE_ONE_LEVEL)
    file(COPY "${RELEASE_PATH}/lib" DESTINATION "${CURRENT_PACKAGES_DIR}")
endif()

if(NOT VCPKG_BUILD_TYPE STREQUAL "release")
    vcpkg_download_distfile(DEBUG_ARCHIVE
        URLS "${WEBRTC_BASE_URL}/webrtc-${VERSION}-${VCPKG_TARGET_ARCHITECTURE}-${WEBRTC_TARGET}-debug.zip"
        FILENAME "webrtc-${VERSION}-${VCPKG_TARGET_ARCHITECTURE}-${WEBRTC_TARGET}-debug.zip"
        SHA512 "${WEBRTC_DEBUG_SHA512}"
    )
    vcpkg_extract_source_archive(DEBUG_PATH ARCHIVE "${DEBUG_ARCHIVE}" NO_REMOVE_ONE_LEVEL)
    file(COPY "${DEBUG_PATH}/lib" DESTINATION "${CURRENT_PACKAGES_DIR}/debug")
endif()

vcpkg_install_copyright(FILE_LIST "${HEADERS_PATH}/LICENSE")
