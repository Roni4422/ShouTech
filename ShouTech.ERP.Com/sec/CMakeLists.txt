cmake_minimum_required(VERSION 3.20)
project(SecEng LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_WINDOWS_EXPORT_ALL_SYMBOLS OFF)

set(SECENG_SOURCES
  src/sec.cpp
  src/prot.cpp
  src/ciph.cpp
  src/crypto.cpp
  src/hashing.cpp
  src/random.cpp
  src/dpapi.cpp
  src/keyvault.cpp
  src/backup.cpp
  src/license.cpp
  src/session.cpp
  src/hardware.cpp
  src/dongle.cpp
  src/integrity.cpp
  src/anti_tamper.cpp
  src/runtime_guard.cpp
  src/diagnostics.cpp
)

add_library(SecEng SHARED ${SECENG_SOURCES})

target_include_directories(SecEng
  PUBLIC  ${CMAKE_CURRENT_SOURCE_DIR}/inc
  PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/internal
)

target_compile_definitions(SecEng PRIVATE ST_SEC_BUILDING_DLL WIN32_LEAN_AND_MEAN NOMINMAX)

target_link_libraries(SecEng PRIVATE
  bcrypt
  crypt32
  setupapi
  hid
  iphlpapi
  shlwapi
)

if(MSVC)
  target_compile_options(SecEng PRIVATE /W4 /permissive- /EHsc /GS /guard:cf)
  target_link_options(SecEng PRIVATE /DYNAMICBASE /HIGHENTROPYVA /NXCOMPAT /GUARD:CF)
else()
  target_compile_options(SecEng PRIVATE -Wall -Wextra -O2)
endif()

set_target_properties(SecEng PROPERTIES
  OUTPUT_NAME "SecEng"
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)
