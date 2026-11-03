// Pinned Jolt 5.6.0 float32, discrete rigid-body SaveState layout.
// Validate all counts, references and finite numbers before Jolt mutates a candidate.
// This intentionally rejects unsupported partial, CCD, joint and soft-body state.
template<class T> T ReadValue(Reader &reader)
{
    T value {};
    reader.Read(value);
    return value;
}

void ReadFinite(Reader &reader, uint32_t count)
{
    for (uint32_t index = 0; index < count; ++index)
        Require(std::isfinite(ReadValue<float>(reader)), "Non-finite internal physics state");
}

void ValidateJoltState(Reader reader, const World &world)
{
    Require(ReadValue<uint8_t>(reader) == uint8_t(EStateRecorderState::All), "Complete Jolt state required");
    float delta = ReadValue<float>(reader);
    Require(std::isfinite(delta) && delta >= 0 && delta <= 1, "Invalid previous physics step");
    const float gravity[] = { 0, -9.81f, 0 };
    for (float value : gravity) Require(ReadValue<float>(reader) == value, "Physics gravity mismatch");
    Require(ReadValue<uint32_t>(reader) == world.bodies.size(), "Internal body count mismatch");
    std::map<uint32_t, const Definition *> definitions;
    for (const auto &body : world.bodies) definitions.emplace(body.second.body.GetIndexAndSequenceNumber(), &body.second.definition);
    std::set<uint32_t> seenBodies;
    for (size_t index = 0; index < world.bodies.size(); ++index)
    {
        uint32_t id = ReadValue<uint32_t>(reader);
        auto body = definitions.find(id);
        Require(body != definitions.end() && seenBodies.insert(id).second, "Invalid internal body identity");
        uint8_t active = ReadValue<uint8_t>(reader);
        Require(active <= 1 && (body->second->dynamic || active == 0), "Invalid body activation");
        ReadFinite(reader, 3);
        Quat rotation;
        reader.Read(rotation);
        Require(rotation.IsNormalized(), "Invalid internal rotation");
        if (body->second->dynamic)
        {
            // Linear/angular velocity, force/torque, three sleep spheres, sleep timer.
            ReadFinite(reader, 25);
            Require(ReadValue<uint8_t>(reader) == 1, "Unsupported sleeping configuration");
        }
    }
    uint32_t pairCount = ReadValue<uint32_t>(reader), manifoldTotal = 0;
    Require(pairCount <= 4096, "Too many cached body pairs");
    std::set<std::pair<uint32_t, uint32_t>> seenPairs;
    for (uint32_t index = 0; index < pairCount; ++index)
    {
        uint32_t first = ReadValue<uint32_t>(reader), second = ReadValue<uint32_t>(reader);
        Require(first != second && definitions.count(first) && definitions.count(second)
            && seenPairs.emplace(std::min(first, second), std::max(first, second)).second, "Invalid cached body pair");
        Require(definitions.at(first)->dynamic || definitions.at(second)->dynamic, "Static bodies cannot contact each other");
        ReadFinite(reader, 6);
        uint32_t count = ReadValue<uint32_t>(reader);
        Require(count <= 4096 - manifoldTotal, "Too many cached manifolds");
        manifoldTotal += count;
        std::set<std::pair<uint32_t, uint32_t>> seenShapes;
        for (uint32_t manifold = 0; manifold < count; ++manifold)
        {
            SubShapeIDPair pair;
            reader.Read(pair);
            Require(pair.GetBody1ID().GetIndexAndSequenceNumber() == first
                && pair.GetBody2ID().GetIndexAndSequenceNumber() == second
                && pair.GetSubShapeID1().GetValue() == 0xffffffff && pair.GetSubShapeID2().GetValue() == 0xffffffff
                && seenShapes.emplace(pair.GetSubShapeID1().GetValue(), pair.GetSubShapeID2().GetValue()).second,
                "Invalid cached subshape pair");
            uint16_t points = ReadValue<uint16_t>(reader);
            Require(points <= 4, "Too many cached contact points");
            ReadFinite(reader, 6 + 7 * points);
        }
    }
    Require(ReadValue<uint32_t>(reader) == 0, "CCD state is unsupported");
    Require(ReadValue<uint32_t>(reader) == 0 && reader.IsEOF(), "Constraints or trailing physics state are unsupported");
}
