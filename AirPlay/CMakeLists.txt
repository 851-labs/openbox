# The AirPlay receiver, airplay.dll, which openbox loads (OpenBox/AirPlay/AirPlayReceiver.cs).
#
# On Windows (CI, .github/workflows/app.yml), with FFmpeg's decoders built as static libraries
# into FFMPEG_DIR (tools/build_ffmpeg.sh):
#
#   cmake -S AirPlay -B build/airplay -A x64 -DFFMPEG_DIR=C:/ffmpeg && cmake --build build/airplay --config Release
#
# On macOS (or Linux), to test the receiver against a real sender without a console, with
# FFmpeg from Homebrew; airplay-test is test/main.c:
#
#   cmake -S AirPlay -B build/airplay && cmake --build build/airplay && build/airplay/airplay-test
cmake_minimum_required(VERSION 3.16)
cmake_policy(SET CMP0091 NEW)
project(openbox_airplay C)

set(CMAKE_C_STANDARD 11)
file(GLOB UXPLAY_SOURCES uxplay/*.c uxplay/llhttp/*.c uxplay/playfair/*.c uxplay/mdnsd/*.c)
add_library(airplay SHARED receiver.c port/crypto.c port/plist.c port/srp.c
    monocypher/monocypher.c monocypher/monocypher-ed25519.c ${UXPLAY_SOURCES})
target_include_directories(airplay PRIVATE uxplay uxplay/llhttp uxplay/playfair port monocypher)
target_compile_definitions(airplay PRIVATE NOHOLD)

if(WIN32)
  # A plain Win32 DLL with the C runtime built in, so it needs nothing from the package; openbox
  # loads it into its app container. port/win32 has the POSIX parts UxPlay expects.
  set_property(TARGET airplay PROPERTY MSVC_RUNTIME_LIBRARY MultiThreaded)
  target_include_directories(airplay PRIVATE port/win32)
  target_compile_options(airplay PRIVATE /FI${CMAKE_CURRENT_SOURCE_DIR}/port/win32/port.h /W3
      /wd4996 /wd4244 /wd4267 /wd4018 /wd4146 /wd4100 /wd4090)
  target_link_libraries(airplay PRIVATE ws2_32 iphlpapi bcrypt)
  if(FFMPEG_DIR)
    target_compile_definitions(airplay PRIVATE OPENBOX_HAVE_FFMPEG)
    target_include_directories(airplay PRIVATE ${FFMPEG_DIR}/include)
    target_link_libraries(airplay PRIVATE ${FFMPEG_DIR}/lib/libavcodec.a ${FFMPEG_DIR}/lib/libavutil.a)
  endif()
else()
  target_compile_options(airplay PRIVATE -Wall -Wno-unused-function -Werror=vla)
  find_package(PkgConfig)
  if(PkgConfig_FOUND)
    pkg_check_modules(FFMPEG libavcodec libavutil)
  endif()
  if(FFMPEG_FOUND)
    target_compile_definitions(airplay PRIVATE OPENBOX_HAVE_FFMPEG)
    target_include_directories(airplay PRIVATE ${FFMPEG_INCLUDE_DIRS})
    target_link_directories(airplay PRIVATE ${FFMPEG_LIBRARY_DIRS})
    target_link_libraries(airplay PRIVATE ${FFMPEG_LIBRARIES})
  endif()
  if(APPLE)
    target_link_libraries(airplay PRIVATE "-framework CoreFoundation")
  endif()
  find_package(Threads REQUIRED)
  target_link_libraries(airplay PRIVATE Threads::Threads)

  add_executable(airplay-test test/main.c)
  target_link_libraries(airplay-test PRIVATE airplay)
endif()
